using Microsoft.Extensions.Logging;
using OnRail;
using OnRail.Extensions.Map;
using OnRail.Extensions.OnSuccess;
using OnRail.ResultDetails.Errors;
using ReadmeGenerator.Cache;
using ReadmeGenerator.Collector;
using ReadmeGenerator.Collector.Models;
using ReadmeGenerator.Crawler;
using ReadmeGenerator.Generator;
using ReadmeGenerator.Helpers;
using ReadmeGenerator.Settings;

namespace ReadmeGenerator;

public class AppRunner(
    AppSettings settings,
    CollectorService collector,
    GeneratorService generator,
    CacheRepository cacheRepository,
    CrawlerService crawler,
    ILogger<AppRunner> logger) {
    public async Task<Result> RunAsync() {
        Utility.SetWorkingDirectory(settings.WorkingDirectory);

        if (!EnsureInputsAreValid(out var validationResult))
            return validationResult;
        logger.LogDebug("App setting values checked.");

        await ConfigUserSettings();

        // Collect problems and solutions
        var problemsResult = await collector.CollectProblemsFromDiskAsync();
        if (!problemsResult.IsSuccess)
            return problemsResult.Map();
        if (problemsResult.Value is null) {
            logger.LogWarning("No problem and solution found!");
            return Result.Ok();
        }

        logger.LogInformation("{Count} problems collected from disk.", problemsResult.Value.Count);

        // Crawl new problems
        await foreach (var newProblem in crawler.CompleteProblemTitlesAsync(problemsResult.Value)) {
            await cacheRepository.SaveAsync(new CacheProblem {
                Id = newProblem.QueraId.ToString(),
                Title = newProblem.QueraTitle!
            });
            logger.LogDebug("{QueraId} cached.", newProblem.QueraId);
        }

        // Order problems
        var problems = problemsResult.Value
            .OrderByDescending(problem => problem.LastSolutionsCommit)
            .ThenBy(problem => problem.QueraId)
            .ToList();

        // Generate readme files and save it
        return await GenerateReadmeFiles(problems);
    }

    private async Task ConfigUserSettings() {
        foreach (var user in settings.Users.Where(user => string.IsNullOrWhiteSpace(user.AvatarUrl))) {
            user.AvatarUrl =
                await Utility.GetDefaultImageAsync(user.PrimaryEmail, user.AliasEmails, settings.DefaultUserProfile!);
        }
    }

    private async Task<Result> GenerateReadmeFiles(List<Problem> problems) {
        // MainPage Readme
        var mainPageReadmeResult = generator.GenerateReadmeSection(problems, settings.MainPageLimit)
            .OnSuccessOperateWhen(() => !string.IsNullOrWhiteSpace(settings.MainPageFooter),
                section => section.AppendLine($"\n{settings.MainPageFooter}"))
            .OnSuccess(section =>
                section.ToString().UseTemplateAsync(settings.ReadmeTemplatePath, "{__REPLACE_WITH_PROGRAM_0__}"))
            .OnSuccess(readme => Utility.SaveDataAsync(
                settings.ReadmeOutputPath, readme, settings.NumberOfTry));

        //CompleteList Readme
        var completeListReadmeResult = generator.GenerateReadmeSection(problems)
            .OnSuccess(section =>
                section.ToString().UseTemplateAsync(settings.CompleteListTemplatePath, "{__REPLACE_WITH_PROGRAM_0__}"))
            .OnSuccess(readme => Utility.SaveDataAsync(
                settings.CompleteListOutputPath, readme, settings.NumberOfTry));

        // Wait to all tasks done
        Task.WaitAll(mainPageReadmeResult, completeListReadmeResult);

        // Return combined results
        return ResultHelpers.CombineResults(await mainPageReadmeResult, await completeListReadmeResult);
    }

    private bool EnsureInputsAreValid(out Result result) {
        if (!File.Exists(settings.ReadmeTemplatePath)) {
            result = Result.Fail(
                new ValidationError(
                    message: $"The readme template path is not valid. ({settings.ReadmeTemplatePath})"));
            return false;
        }

        if (!Directory.Exists(settings.SolutionsPath)) {
            result = Result.Fail(
                new ValidationError(
                    message: $"The solutions directory is not valid. ({settings.SolutionsPath})"));
            return false;
        }

        var usersWithoutPrimaryEmail = settings.Users.Count(user => string.IsNullOrWhiteSpace(user.PrimaryEmail));
        if (usersWithoutPrimaryEmail > 0) {
            result = Result.Fail(new ValidationError(
                message:
                $"{nameof(UserSetting.PrimaryEmail)} is required for users in app settings. {usersWithoutPrimaryEmail} users have not the {nameof(UserSetting.PrimaryEmail)}")
            );
            return false;
        }

        if (string.IsNullOrWhiteSpace(settings.DefaultUserProfile)) {
            result = Result.Fail(new ValidationError(
                message: $"'{nameof(settings.DefaultUserProfile)}' can not be null or empty.")
            );
            return false;
        }

        if (settings.Problems.Any(problem => {
                return problem.Contributors.Any(contributor =>
                    string.IsNullOrWhiteSpace(contributor.UserName)
                    || string.IsNullOrWhiteSpace(contributor.AvatarUrl)
                    || string.IsNullOrWhiteSpace(contributor.ProfileUrl));
            })) {
            result = Result.Fail(new ValidationError(
                message: $"'{nameof(settings.Problems)}' are not valid.")
            );
            return false;
        }

        result = Result.Ok();
        return true;
    }
}