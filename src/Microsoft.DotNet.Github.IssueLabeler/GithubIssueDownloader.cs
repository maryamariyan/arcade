// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Octokit;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.DotNet.Github.IssueLabeler
{
    public class GithubIssueDownloader
    {
        private GitHubClient _client;
        public string _repoName;
        private string _owner;
        private int _startIndex;
        private int _endIndex;
        private string _outputFile;
  
        public GithubIssueDownloader(string authToken, string repoName, string owner, int startIndex, int endIndex, string OutputFile)
        {
            if (startIndex <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(startIndex));
            }

            if (endIndex <= 0 || endIndex < startIndex)
            {
                throw new ArgumentOutOfRangeException(nameof(endIndex));
            }

            _client = new GitHubClient(new ProductHeaderValue("GithubIssueDownloader"))
            {
                Credentials = new Credentials(authToken)
            };

            _repoName = repoName;
            _owner = owner;
            _startIndex = startIndex;
            _endIndex = endIndex;
            _outputFile = OutputFile;
        }

        public async Task DownloadAndSaveAsync()
        {
            StringBuilder sb = new StringBuilder();
            if (!File.Exists(_outputFile))
                File.WriteAllText(_outputFile, "Area\tTitle\tDescription\tIsPR\tFilePaths" + Environment.NewLine);
            for (int i = _startIndex; i < _endIndex; i++)
            {
                string filePaths = string.Empty;
                bool isPr = true;
                Issue issueOrPr = null;
                try
                {
                    var prFiles = await _client.PullRequest.Files(_owner, _repoName, i);
                    filePaths = String.Join(";", prFiles.Select(x => x.FileName));
                    issueOrPr = await _client.Issue.Get(_owner, _repoName, i);
                }
                catch (NotFoundException ex)
                {
                    if (ex.Message.Contains("files was not found."))
                        isPr = false;
                    else
                        continue;
                }
                catch (RateLimitExceededException)
                {
                    Thread.Sleep(3_600_000);
                    i--;
                    continue;
                }

                foreach (var label in issueOrPr.Labels)
                {
                    if (label.Name.Contains("area-"))
                    {
                        string title = RemoveNewLineCharacters(issueOrPr.Title);
                        string description = RemoveNewLineCharacters(issueOrPr.Body);
                        // Ordering is important here because we are using the same ordering on the prediction side.
                        string curLabel = label.Name;
                        sb.AppendLine($"{curLabel}\t\"{title}\"\t\"{description}\"\t{isPr}\t{filePaths}");
                    }
                }

                if (i % 1000 == 0)
                {
                    File.AppendAllText(_outputFile, sb.ToString());
                    sb.Clear();
                }
            }
            File.AppendAllText(_outputFile, sb.ToString());
        }

        private static string RemoveNewLineCharacters(string input)
        {
            return input?.Replace("\r\n", " ").Replace("\n", " ").Replace("\t", " ");
        }
    }
}
