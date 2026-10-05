using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using LetterboxdSync;
using LetterboxdSync.Api;
using LetterboxdSync.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// ntfy push notifications: request shape, disabled-by-default, dedupe, failure isolation,
/// and the two hook points (auth breaker opening, a film being abandoned).
/// </summary>
[Collection("Plugin")]
public class NotifierTests : IDisposable
{
    private const string TopicUrl = "https://ntfy.example.com/jellyscribe";
    private const string Password = "hunter2-very-secret";

    private readonly string _tempDir;
    private readonly HttpClient _originalHttp;
    private readonly NtfyHandler _handler;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly LetterboxdSyncRunner _runner;

    public NotifierTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-ntfy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var paths = Substitute.For<IApplicationPaths>();
        paths.PluginConfigurationsPath.Returns(_tempDir);
        paths.LogDirectoryPath.Returns(_tempDir);
        paths.DataPath.Returns(_tempDir);
        paths.CachePath.Returns(_tempDir);
        var xml = Substitute.For<IXmlSerializer>();
        xml.DeserializeFromFile(typeof(PluginConfiguration), Arg.Any<string>())
            .Returns(_ => new PluginConfiguration());
        new Plugin(paths, xml);

        SyncHistory.DataPathOverride = Path.Combine(_tempDir, "sync-history.jsonl");
        SyncHistory.ResetForTesting();
        AuthBreaker.DataPathOverride = Path.Combine(_tempDir, "auth-breaker.json");
        AuthBreaker.ResetForTesting();

        _handler = new NtfyHandler();
        _originalHttp = Notifier.Http;
        Notifier.Http = new HttpClient(_handler);
        Notifier.ResetForTesting();

        _userManager = Substitute.For<IUserManager>();
        _libraryManager = Substitute.For<ILibraryManager>();
        _userDataManager = Substitute.For<IUserDataManager>();
        _runner = new LetterboxdSyncRunner(NullLoggerFactory.Instance, _libraryManager, _userManager, _userDataManager);
    }

    public void Dispose()
    {
        Notifier.Http = _originalHttp;
        Notifier.ResetForTesting();
        LetterboxdServiceFactory.OverrideForTesting = null;
        SyncHistory.DataPathOverride = null;
        SyncHistory.ResetForTesting();
        AuthBreaker.DataPathOverride = null;
        AuthBreaker.ResetForTesting();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    private static void Enable(string? token = null)
    {
        Plugin.Instance!.Configuration.NtfyUrl = TopicUrl;
        Plugin.Instance!.Configuration.NtfyToken = token;
    }

    [Fact]
    public async Task SendAsync_PostsPlainTextWithNtfyHeadersAndBearerToken()
    {
        await Notifier.SendAsync(TopicUrl, "tk_abc", "Title here", "Body here", "high");

        var r = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Post, r.Method);
        Assert.Equal(TopicUrl, r.Url);
        Assert.Equal("Body here", r.Body);
        Assert.Equal("text/plain", r.ContentType);
        Assert.Equal("Title here", r.Headers["Title"]);
        Assert.Equal("warning", r.Headers["Tags"]);
        Assert.Equal("high", r.Headers["Priority"]);
        Assert.Equal("Bearer tk_abc", r.Headers["Authorization"]);
    }

    [Fact]
    public async Task SendAsync_NoToken_SendsNoAuthorizationHeader()
    {
        await Notifier.SendAsync(TopicUrl, null, "t", "m", "default");

        Assert.False(Assert.Single(_handler.Requests).Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task SendAsync_ErrorStatus_Throws()
    {
        _handler.Status = HttpStatusCode.Forbidden;

        await Assert.ThrowsAsync<HttpRequestException>(() => Notifier.SendAsync(TopicUrl, null, "t", "m", "default"));
    }

    [Fact]
    public async Task EmptyUrl_SendsNothing()
    {
        await Notifier.LetterboxdLoginFailedAsync("lachlan", "kostadamus", "bad password", NullLogger.Instance);

        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task SameEvent_IsSentOncePerWindow_OtherEventsStillSend()
    {
        Enable();

        await Notifier.LetterboxdLoginFailedAsync("lachlan", "kostadamus", "bad password", NullLogger.Instance);
        await Notifier.LetterboxdLoginFailedAsync("lachlan", "KOSTADAMUS", "bad password", NullLogger.Instance);
        await Notifier.LetterboxdLoginFailedAsync("lachlan", "otheraccount", "bad password", NullLogger.Instance);
        await Notifier.GaveUpAsync("Sinners", "lachlan", 1233413, 3, "404", NullLogger.Instance);
        await Notifier.GaveUpAsync("Sinners", "lachlan", 1233413, 3, "404", NullLogger.Instance);

        Assert.Equal(3, _handler.Requests.Count);
    }

    [Fact]
    public async Task SendFailure_DoesNotThrow_AndNextOccurrenceRetries()
    {
        Enable();
        _handler.Throw = true;

        await Notifier.LetterboxdLoginFailedAsync("lachlan", "kostadamus", "bad password", NullLogger.Instance);

        _handler.Throw = false;
        await Notifier.LetterboxdLoginFailedAsync("lachlan", "kostadamus", "bad password", NullLogger.Instance);

        Assert.Single(_handler.Requests);
    }

    private (User User, string IdHex) SetUpUserWithPlayedMovie()
    {
        var user = new User("lachlan", "test-provider-id", "test-reset-id");
        var userId = user.Id.ToString("N");
        _userManager.GetUsers().Returns(new[] { user });

        Plugin.Instance!.Configuration.Accounts.Add(new Account
        {
            UserJellyfinId = userId,
            LetterboxdUsername = "kostadamus",
            LetterboxdPassword = Password,
            Enabled = true,
            SkipPreviouslySynced = false
        });

        var movie = new Movie { Name = "Sinners", Id = Guid.NewGuid() };
        movie.SetProviderId(MetadataProvider.Tmdb, "1233413");
        _libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem> { movie });
        _userDataManager.GetUserData(user, movie).Returns(new UserItemData
        {
            Key = "k",
            Played = true,
            LastPlayedDate = DateTime.UtcNow.AddHours(-1)
        });

        return (user, userId);
    }

    [Fact]
    public async Task AuthBreakerOpening_SendsOneHighPriorityNotification()
    {
        Enable();
        var (_, userId) = SetUpUserWithPlayedMovie();
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) =>
            throw new InvalidOperationException("Login failed: bad credentials");

        for (var i = 0; i < AuthBreaker.Threshold; i++)
        {
            await _runner.TryRunForUserAsync(userId, "test", new Progress<double>(), CancellationToken.None);
            Assert.Equal(i == AuthBreaker.Threshold - 1 ? 1 : 0, _handler.Requests.Count);
        }

        var r = Assert.Single(_handler.Requests);
        Assert.Equal("high", r.Headers["Priority"]);
        Assert.Contains("lachlan", r.Body);
        Assert.Contains("kostadamus", r.Body);
        Assert.Contains("bad credentials", r.Body);
        Assert.DoesNotContain(Password, r.Body);
    }

    [Fact]
    public async Task FilmAbandoned_NotifiesOnTheRunThatGivesUp()
    {
        Enable();
        var (_, userId) = SetUpUserWithPlayedMovie();
        var service = Substitute.For<ILetterboxdService>();
        service.LookupFilmByTmdbIdAsync(Arg.Any<int>())
            .Returns<Task<FilmResult>>(_ => throw new InvalidOperationException("film not found"));
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);

        for (var run = 1; run <= LetterboxdSyncRunner.MaxConsecutiveSyncFailures + 1; run++)
        {
            await _runner.TryRunForUserAsync(userId, "test", new Progress<double>(), CancellationToken.None);
            Assert.Equal(run >= LetterboxdSyncRunner.MaxConsecutiveSyncFailures ? 1 : 0, _handler.Requests.Count);
        }

        var r = Assert.Single(_handler.Requests);
        Assert.Equal("default", r.Headers["Priority"]);
        Assert.Contains("Gave up on Sinners for lachlan after 3 failed attempts: film not found", r.Body);
    }

    [Fact]
    public void TestNotification_RequiresElevation()
    {
        var attr = typeof(LetterboxdController).GetMethod(nameof(LetterboxdController.TestNotification))!
            .GetCustomAttribute<AuthorizeAttribute>();

        Assert.Equal("RequiresElevation", attr?.Policy);
    }

    [Fact]
    public async Task TestNotification_SendsWithFormValues()
    {
        using var h = new ControllerTestHarness();

        var result = await h.Controller.TestNotification(new NtfyTestRequest { Url = TopicUrl, Token = "tk_abc" });

        Assert.IsType<OkObjectResult>(result);
        var r = Assert.Single(_handler.Requests);
        Assert.Equal(TopicUrl, r.Url);
        Assert.Equal("Bearer tk_abc", r.Headers["Authorization"]);
    }

    [Fact]
    public async Task TestNotification_MissingUrlOrSendFailure_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness();

        Assert.IsType<BadRequestObjectResult>(await h.Controller.TestNotification(new NtfyTestRequest()));

        _handler.Status = HttpStatusCode.Unauthorized;
        Assert.IsType<BadRequestObjectResult>(await h.Controller.TestNotification(new NtfyTestRequest { Url = TopicUrl }));
    }

    private sealed record Captured(HttpMethod Method, string Url, string Body, string? ContentType, Dictionary<string, string> Headers);

    private sealed class NtfyHandler : HttpMessageHandler
    {
        public List<Captured> Requests { get; } = new();

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public bool Throw { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Throw)
                throw new HttpRequestException("connection refused");

            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));
            var body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Captured(request.Method, request.RequestUri!.ToString(), body,
                request.Content?.Headers.ContentType?.MediaType, headers));
            return new HttpResponseMessage(Status);
        }
    }
}
