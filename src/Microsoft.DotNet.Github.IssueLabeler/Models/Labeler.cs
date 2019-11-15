// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Azure.KeyVault;
using Microsoft.Azure.KeyVault.Models;
using Microsoft.Azure.Services.AppAuthentication;
using Microsoft.DotNet.Github.IssueLabeler.Helpers;
using Microsoft.Extensions.Logging;
using Octokit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Microsoft.DotNet.GitHub.IssueLabeler
{
    public class Labeler
    {
        private GitHubClient _client;
        private Regex _regex;
        private readonly string _repoOwner;
        private readonly string _repoName;
        private readonly double _threshold;
        private readonly string _secretUri;
        private readonly DiffHelper _diffHelper;
        private readonly DatasetHelper _datasetHelper;

        public Labeler(string repoOwner, string repoName, string secretUri, double threshold, DiffHelper diffHelper, DatasetHelper datasetHelper)
        {
            _repoOwner = repoOwner;
            _repoName = repoName;
            _threshold = threshold;
            _secretUri = secretUri;
            _diffHelper = diffHelper;
            _datasetHelper = datasetHelper;
        }

        private async Task GitSetupAsync()
        {
            AzureServiceTokenProvider azureServiceTokenProvider = new AzureServiceTokenProvider();
            KeyVaultClient keyVaultClient = new KeyVaultClient(new KeyVaultClient.AuthenticationCallback(azureServiceTokenProvider.KeyVaultTokenCallback));
            SecretBundle secretBundle = await keyVaultClient.GetSecretAsync(_secretUri).ConfigureAwait(false);

            var productInformation = new ProductHeaderValue("MLGitHubLabeler");
            _client = new GitHubClient(productInformation)
            {
                Credentials = new Credentials(secretBundle.Value)
            };
        }

        public async Task PredictAndApplyLabelAsync(int number, GithubObjectType issueOrPr, ILogger logger)
        {
            string label = await PredictLabelAsync(number, _repoName, issueOrPr, logger);

            Issue issueGithubVersion = await _client.Issue.Get(_repoOwner, _repoName, number);
            var labelNames = issueGithubVersion.Labels.Where(x => !string.IsNullOrEmpty(x.Name)).Select(x => x.Name);
            if (label != null && !labelNames.Where(x => x.StartsWith("area-")).Any())
            {
                var issueUpdate = new IssueUpdate();
                issueUpdate.AddLabel(label);
                if (!labelNames.Contains("untriaged") && issueOrPr == GithubObjectType.Issue)
                {
                    issueUpdate.AddLabel("untriaged");
                }
                issueUpdate.Milestone = issueGithubVersion.Milestone?.Number; // The number of milestone associated with the issue.

                await _client.Issue.Update(_repoOwner, _repoName, number, issueUpdate);
            }
            else
            {
                logger.LogInformation($"! The Model is not able to assign the label to the {issueOrPr} {number} confidently.");
            }
        }

        internal async Task<string> PredictLabelAsync(int number, string repo, GithubObjectType issueOrPr, ILogger logger)
        {
            if (_client == null)
            {
                await GitSetupAsync();
            }
            if (_regex == null)
            {
                _regex = new Regex(@"@[a-zA-Z0-9_//-]+");
            }
            var iop = await _client.Issue.Get(_repoOwner, repo, number);
            var userMentions = _regex.Matches(iop.Body).Select(x => x.Value).ToArray();

            string label;
            if (issueOrPr == GithubObjectType.Issue)
            {
                IssueModel issue = CreateIssue(number, iop.Title, iop.Body, userMentions, iop.User.Login);
                label = Predictor.Predict(issue, logger, _threshold);
            }
            else
            {
                PrModel pr = await CreatePullRequest(number, iop.Title, iop.Body, userMentions, iop.User.Login);
                label = Predictor.Predict(pr, logger, _threshold);
            }

            return label;
        }

        private static IssueModel CreateIssue(int number, string title, string body, string[] userMentions, string author)
        {
            return new IssueModel()
            {
                Number = number,
                Title = title,
                Body = body,
                IsPR = 0,
                IssueAuthor = author,
                UserMentions = string.Join(' ', userMentions),
                NumMentions = userMentions.Length
            };
        }

        private async Task<PrModel> CreatePullRequest(int number, string title, string body, string[] userMentions, string author)
        {
            var pr = new PrModel()
            {
                Number = number,
                Title = title,
                Body = body,
                IsPR = 1,
                IssueAuthor = author,
                UserMentions = string.Join(' ', userMentions),
                NumMentions = userMentions.Length,
            };
            IReadOnlyList<PullRequestFile> prFiles = await _client.PullRequest.Files(_repoOwner, _repoName, number);
            if (prFiles.Count != 0)
            {
                string[] filePaths = prFiles.Select(x => x.FileName).ToArray();
                _diffHelper.ResetTo(filePaths);
                pr.Files = _datasetHelper.FlattenIntoColumn(filePaths);
                pr.Filenames = _datasetHelper.FlattenIntoColumn(_diffHelper.Filenames);
                pr.FileExtensions = _datasetHelper.FlattenIntoColumn(_diffHelper.Extensions);
                pr.Folders = _datasetHelper.FlattenIntoColumn(_diffHelper.Folders);
                pr.FolderNames = _datasetHelper.FlattenIntoColumn(_diffHelper.FolderNames);
            }
            pr.FileCount = prFiles.Count;
            return pr;
        }
    }
}
