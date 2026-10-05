using System;
using System.IO;
using System.Threading.Tasks;
using LetterboxdSync.Serializd;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace LetterboxdSync.Tests.Serializd;

[Collection("Plugin")]
public class SerializdShowStatusTests : IDisposable
{
    private const string UserId = "0f8fad5bd9cb469fa16570867728950e";
    private const string Email = "me@example.com";
    private const int Show = 220542;
    private static readonly ILogger Log = NullLogger.Instance;

    private readonly string _tempDir;

    public SerializdShowStatusTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-szstatus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        SerializdSyncHistory.DataPathOverride = Path.Combine(_tempDir, "sz-history.jsonl");
        SerializdSyncHistory.ResetForTesting();
    }

    public void Dispose()
    {
        SerializdSyncHistory.DataPathOverride = null;
        SerializdSyncHistory.ResetForTesting();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public async Task FirstEpisodeOfAShow_MarksItCurrentlyWatchingOnce()
    {
        var service = Substitute.For<ISerializdService>();

        Assert.True(await SerializdShowStatus.MarkCurrentlyWatchingAsync(service, UserId, Email, Show, () => false));
        Assert.False(await SerializdShowStatus.MarkCurrentlyWatchingAsync(service, UserId, Email, Show, () => false));

        await service.Received(1).SetCurrentlyWatchingAsync(Show);
    }

    [Fact]
    public async Task FinishedShow_IsLeftAlone()
    {
        var service = Substitute.For<ISerializdService>();

        Assert.False(await SerializdShowStatus.MarkCurrentlyWatchingAsync(service, UserId, Email, Show, () => true));

        await service.DidNotReceive().SetCurrentlyWatchingAsync(Arg.Any<int>());
        Assert.False(SerializdSyncHistory.Has(UserId, Email, Show, 0, 0, SerializdSyncHistory.KindCurrentlyWatching));
        Assert.True(SerializdSyncHistory.Has(UserId, Email, Show, 0, 0, SerializdSyncHistory.KindFinished));
    }

    [Fact]
    public void FinishedShow_IsOnlyCheckedOnce()
    {
        var checks = 0;
        bool Finished() { checks++; return true; }

        Assert.True(SerializdShowStatus.IsPending(UserId, Email, Show, Finished));
        Assert.True(SerializdShowStatus.IsPending(UserId, Email, Show, Finished));

        Assert.Equal(1, checks);
    }

    [Fact]
    public async Task FinishedMarker_IsTrackedPerAccount()
    {
        var service = Substitute.For<ISerializdService>();
        SerializdShowStatus.IsPending(UserId, Email, Show, () => true);

        Assert.False(await SerializdShowStatus.MarkCurrentlyWatchingAsync(service, UserId, Email, Show, () => false));
        Assert.True(await SerializdShowStatus.MarkCurrentlyWatchingAsync(service, UserId, "other@example.com", Show, () => false));
    }

    [Fact]
    public async Task FinishedShow_IsMarkedWatchedOnce_AndTakenOffCurrentlyWatching()
    {
        var service = Substitute.For<ISerializdService>();

        Assert.True(await SerializdShowStatus.MarkWatchedAsync(service, UserId, Email, Show, () => true, Log));
        Assert.False(await SerializdShowStatus.MarkWatchedAsync(service, UserId, Email, Show, () => true, Log));

        await service.Received(1).SetWatchedAsync(Show);
        await service.Received(1).RemoveCurrentlyWatchingAsync(Show);
        Assert.False(SerializdShowStatus.IsPending(UserId, Email, Show, () => true));
    }

    [Fact]
    public async Task CurrentlyWatchingShowThatFinishes_IsPendingForTheWatchedMark()
    {
        SerializdSyncHistory.Record(UserId, Email, Show, 0, 0, SerializdSyncHistory.KindCurrentlyWatching);
        var service = Substitute.For<ISerializdService>();

        Assert.True(SerializdShowStatus.IsPending(UserId, Email, Show, () => true));
        Assert.True(await SerializdShowStatus.MarkWatchedAsync(service, UserId, Email, Show, () => true, Log));
    }

    [Fact]
    public async Task ContinuingShow_IsNeverMarkedWatched()
    {
        var service = Substitute.For<ISerializdService>();

        Assert.False(await SerializdShowStatus.MarkWatchedAsync(service, UserId, Email, Show, () => false, Log));

        await service.DidNotReceive().SetWatchedAsync(Arg.Any<int>());
        await service.DidNotReceive().RemoveCurrentlyWatchingAsync(Arg.Any<int>());
    }

    [Fact]
    public async Task FailedWatchedCall_IsRetriedNextTime()
    {
        var service = Substitute.For<ISerializdService>();
        service.SetWatchedAsync(Show).Returns(Task.FromException(new Exception("500")), Task.CompletedTask);

        await Assert.ThrowsAsync<Exception>(() => SerializdShowStatus.MarkWatchedAsync(service, UserId, Email, Show, () => true, Log));

        Assert.False(SerializdSyncHistory.Has(UserId, Email, Show, 0, 0, SerializdSyncHistory.KindShowWatched));
        Assert.True(SerializdShowStatus.IsPending(UserId, Email, Show, () => true));
        await service.DidNotReceive().RemoveCurrentlyWatchingAsync(Arg.Any<int>());
        Assert.True(await SerializdShowStatus.MarkWatchedAsync(service, UserId, Email, Show, () => true, Log));
    }

    [Fact]
    public async Task FailedRemoveCall_StillRecordsWatched()
    {
        var service = Substitute.For<ISerializdService>();
        service.RemoveCurrentlyWatchingAsync(Show).Returns(Task.FromException(new Exception("404")));

        Assert.True(await SerializdShowStatus.MarkWatchedAsync(service, UserId, Email, Show, () => true, Log));

        Assert.True(SerializdSyncHistory.Has(UserId, Email, Show, 0, 0, SerializdSyncHistory.KindShowWatched));
        Assert.False(SerializdShowStatus.IsPending(UserId, Email, Show, () => true));
    }

    [Fact]
    public async Task WatchedMarker_IsTrackedPerAccount()
    {
        var service = Substitute.For<ISerializdService>();

        await SerializdShowStatus.MarkWatchedAsync(service, UserId, Email, Show, () => true, Log);
        Assert.True(await SerializdShowStatus.MarkWatchedAsync(service, UserId, "other@example.com", Show, () => true, Log));

        await service.Received(2).SetWatchedAsync(Show);
    }

    [Fact]
    public async Task FailedCall_IsRetriedNextTime()
    {
        var service = Substitute.For<ISerializdService>();
        service.SetCurrentlyWatchingAsync(Show).Returns(Task.FromException(new Exception("500")));

        await Assert.ThrowsAsync<Exception>(() => SerializdShowStatus.MarkCurrentlyWatchingAsync(service, UserId, Email, Show, () => false));

        Assert.True(SerializdShowStatus.IsPending(UserId, Email, Show, () => false));
    }

    [Fact]
    public void StatusIsTrackedPerAccount()
    {
        SerializdSyncHistory.Record(UserId, Email, Show, 0, 0, SerializdSyncHistory.KindCurrentlyWatching);

        Assert.False(SerializdShowStatus.IsPending(UserId, Email, Show, () => false));
        Assert.True(SerializdShowStatus.IsPending(UserId, "other@example.com", Show, () => false));
    }

    [Fact]
    public void WithoutASeries_TheShowIsNotFinished()
    {
        Assert.False(SerializdShowStatus.IsFinished(null, _ => true));
    }

    [Fact]
    public void AShowStillAiring_IsNotFinishedEvenWhenCaughtUp()
    {
        Assert.False(SerializdShowStatus.IsFinished(new Series { Status = SeriesStatus.Continuing }, _ => true));
    }
}
