using System.Collections.Generic;
using System.Threading.Tasks;
using LetterboxdSync.Serializd;
using NSubstitute;
using Xunit;

namespace LetterboxdSync.Tests.Serializd;

public class SerializdSeasonFallbackTests
{
    private const int Show = 220542;

    private static readonly IReadOnlyDictionary<int, int> TwoSeasonsOf24 = new Dictionary<int, int> { [1] = 24, [2] = 24 };

    private static ISerializdService ServiceWithSeasons(params int[] seasonNumbers)
    {
        var service = Substitute.For<ISerializdService>();
        service.ResolveSeasonIdAsync(Arg.Any<int>(), Arg.Any<int>()).Returns(Task.FromResult<int?>(null));
        foreach (var n in seasonNumbers)
            service.ResolveSeasonIdAsync(Show, n).Returns(Task.FromResult<int?>(9000 + n));
        return service;
    }

    [Fact]
    public async Task SeasonSerializdKnows_IsUsedAsIs()
    {
        var target = await SerializdSeasonFallback.ResolveAsync(ServiceWithSeasons(1, 2), Show, 2, () => TwoSeasonsOf24);

        Assert.Equal(new SerializdSeasonTarget(9002, 0), target);
    }

    [Fact]
    public async Task ShowSerializdKeepsAsOneSeason_LogsToSeasonOneAtTheAbsoluteNumber()
    {
        var target = await SerializdSeasonFallback.ResolveAsync(ServiceWithSeasons(1), Show, 2, () => TwoSeasonsOf24);

        Assert.Equal(new SerializdSeasonTarget(9001, 24), target);
    }

    [Fact]
    public async Task ThirdSeason_CountsBothEarlierSeasons()
    {
        var lengths = new Dictionary<int, int> { [1] = 24, [2] = 23, [3] = 12 };

        var target = await SerializdSeasonFallback.ResolveAsync(ServiceWithSeasons(0, 1), Show, 3, () => lengths);

        Assert.Equal(new SerializdSeasonTarget(9001, 47), target);
    }

    [Fact]
    public async Task ShowSerializdSplitsIntoSeasons_NeverFallsBack()
    {
        var target = await SerializdSeasonFallback.ResolveAsync(ServiceWithSeasons(1, 2), Show, 3,
            () => new Dictionary<int, int> { [1] = 24, [2] = 23 });

        Assert.Null(target);
    }

    [Fact]
    public async Task UnknownEarlierSeasonLength_DoesNotGuess()
    {
        var target = await SerializdSeasonFallback.ResolveAsync(ServiceWithSeasons(1), Show, 3,
            () => new Dictionary<int, int> { [2] = 23 });

        Assert.Null(target);
    }

    [Fact]
    public async Task MissingSeasonOneOrSpecials_HaveNothingToFallBackTo()
    {
        var service = ServiceWithSeasons();

        Assert.Null(await SerializdSeasonFallback.ResolveAsync(service, Show, 2, () => TwoSeasonsOf24));
        Assert.Null(await SerializdSeasonFallback.ResolveAsync(service, Show, 1, () => TwoSeasonsOf24));
        Assert.Null(await SerializdSeasonFallback.ResolveAsync(service, Show, 0, () => TwoSeasonsOf24));
    }

    [Fact]
    public async Task SeasonLengths_AreOnlyReadWhenFallingBack()
    {
        var read = false;

        await SerializdSeasonFallback.ResolveAsync(ServiceWithSeasons(1, 2), Show, 2, () =>
        {
            read = true;
            return TwoSeasonsOf24;
        });

        Assert.False(read);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 24)]
    [InlineData(3, 48)]
    public void EpisodeOffset_SumsTheEarlierSeasons(int season, int expected)
    {
        var lengths = new Dictionary<int, int> { [1] = 24, [2] = 24, [3] = 12 };

        Assert.Equal(expected, SerializdSeasonFallback.EpisodeOffset(season, lengths));
    }

    [Fact]
    public void SeasonLengths_WithoutASeries_AreEmpty()
    {
        Assert.Empty(SerializdSeasonFallback.ReadSeasonLengths(null));
    }
}
