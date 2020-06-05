// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Extensions.Logging;
using Microsoft.ML;
using System.Linq;

namespace Microsoft.DotNet.GitHub.IssueLabeler
{
    internal static class Predictor
    {
        private static string PrModelPath => @"model\GitHubPrLabelerModel.zip";
        private static string IssueModelPath => @"model\GitHubIssueLabelerModel.zip";
        private static string GeneralizedPrModelPath => @"model\GeneralizedGitHubPrLabelerModel.zip";
        private static string GeneralizedIssueModelPath => @"model\GeneralizedGitHubIssueLabelerModel.zip";
        private static string ExtensionsTransferredIssueModelPath => @"model\ExtensionsTransferredIssueModelPath.zip";
        private static PredictionEngine<IssueModel, GitHubIssuePrediction> issuePredEngine;
        private static PredictionEngine<PrModel, GitHubIssuePrediction> prPredEngine;
        private static PredictionEngine<IssueModel, GitHubIssuePrediction> generalizedIssuePredEngine;
        private static PredictionEngine<PrModel, GitHubIssuePrediction> generalizedPrPredEngine;
        private static PredictionEngine<IssueModel, GitHubIssuePrediction> extensionIssueTransferredPredEngine;

        public static string Predict(IssueModel issue, ILogger logger, double threshold, bool useAlternative = false)
        {
            return Predict(issue, ref issuePredEngine, IssueModelPath, ref generalizedIssuePredEngine, GeneralizedIssueModelPath, ref extensionIssueTransferredPredEngine, ExtensionsTransferredIssueModelPath, logger, threshold, useAlternative);
        }

        public static string Predict(PrModel issue, ILogger logger, double threshold)
        {
            return Predict(issue, ref prPredEngine, PrModelPath, ref generalizedPrPredEngine, GeneralizedPrModelPath, ref extensionIssueTransferredPredEngine, ExtensionsTransferredIssueModelPath, logger, threshold, false);
        }

        public static string Predict<T>(
            T issueOrPr, 
            ref PredictionEngine<T, GitHubIssuePrediction> predEngine, 
            string modelPath, 
            ref PredictionEngine<T, GitHubIssuePrediction> generalizedPredEngine, 
            string generalizedModelPath,
            ref PredictionEngine<IssueModel, GitHubIssuePrediction> extensionTransferredPredEngine, 
            string extensionTransferredModelPath,
            ILogger logger, 
            double threshold, bool useAlternative = false) 
            where T : IssueModel
        {
            if (predEngine == null)
            {
                MLContext mlContext = new MLContext();
                ITransformer mlModel = mlContext.Model.Load(modelPath, out DataViewSchema inputSchema);
                predEngine = mlContext.Model.CreatePredictionEngine<T, GitHubIssuePrediction>(mlModel);
            }

            GitHubIssuePrediction prediction = predEngine.Predict(issueOrPr);
            float[] probabilities = prediction.Score;
            float maxProbability = probabilities.Max();
            logger.LogInformation($"# {maxProbability} {prediction.Area} for #{issueOrPr.Number} {issueOrPr.Title}");
            if (maxProbability > threshold && !useAlternative)
            {
                return prediction.Area;
            }
            var toldYouSo = maxProbability > threshold ? prediction.Area : null;

            if (useAlternative)
            {
                // model for extensions assuming transferred from extension

                if (extensionTransferredPredEngine == null)
                {
                    MLContext mlContext = new MLContext();
                    ITransformer mlModel = mlContext.Model.Load(extensionTransferredModelPath, out DataViewSchema inputSchema);
                    extensionTransferredPredEngine = mlContext.Model.CreatePredictionEngine<IssueModel, GitHubIssuePrediction>(mlModel);
                }
                prediction = extensionTransferredPredEngine.Predict(issueOrPr);
                probabilities = prediction.Score;
                maxProbability = probabilities.Max();
                logger.LogInformation($"# extensions transferred: {maxProbability} {prediction.Area} for #{issueOrPr.Number} {issueOrPr.Title}");
                if (prediction.Area.StartsWith("area-Extensions"))
                {
                    var ret = prediction.Area;
                    if (ret.StartsWith("area-Extensions."))
                        ret = prediction.Area.Replace("area-Extensions.", "area-Extensions-");
                        return maxProbability > threshold ? ret : null;
                }
                else if (toldYouSo != null)
                {
                    return toldYouSo;
                }
            }

            if (generalizedPredEngine == null)
            {
                MLContext mlContext = new MLContext();
                ITransformer mlModel = mlContext.Model.Load(generalizedModelPath, out DataViewSchema inputSchema);
                generalizedPredEngine = mlContext.Model.CreatePredictionEngine<T, GitHubIssuePrediction>(mlModel);
            }
            prediction = generalizedPredEngine.Predict(issueOrPr);
            probabilities = prediction.Score;
            maxProbability = probabilities.Max();
            logger.LogInformation($"# generalized: {maxProbability} {prediction.Area} for #{issueOrPr.Number} {issueOrPr.Title}");
            // TODO analyze if ever this was useful.
            if (
                (prediction.Area.Equals("area-Infrastructure") && !issueOrPr.IssueAuthor.Equals("jaredpar"))
                || 
                prediction.Area.Equals("area-Runtime")
                )
                return null;

            return maxProbability > threshold ? prediction.Area : null;
        }
    }
}
