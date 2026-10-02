// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System.Linq;
using Grog.Core.Download;
using Grog.Core.Tests.Framework;

/// <summary>Auto concurrency picks from the queue head's median size: small = 4, medium = 3,
/// large = 2, capped by what is left, one file = one worker, unknown sizes count as small.</summary>
[NewBatch]
[Trait("download")]
public class ConcurrencyAdvisorTests
{
    private const long MB = 1L << 20;

    [Test]
    public void Small_files_get_four_workers()
        => Assert.Equal(4, ConcurrencyAdvisor.Pick(Enumerable.Repeat(1 * MB, 10).ToList(), 600));

    [Test]
    public void Medium_files_get_three()
        => Assert.Equal(3, ConcurrencyAdvisor.Pick(Enumerable.Repeat(100 * MB, 10).ToList(), 40));

    [Test]
    public void Large_installers_get_two()
        => Assert.Equal(2, ConcurrencyAdvisor.Pick(Enumerable.Repeat(4000 * MB, 10).ToList(), 12));

    [Test]
    public void Mixed_head_follows_the_median_not_the_biggest_file()
    {
        var head = new[] { 4000 * MB, 1 * MB, 1 * MB, 1 * MB, 1 * MB, 2 * MB, 1 * MB, 1 * MB, 1 * MB, 1 * MB };
        Assert.Equal(4, ConcurrencyAdvisor.Pick(head, 50), "one big installer beside small files still parallelizes the small ones");
    }

    [Test]
    public void Never_more_workers_than_files_left()
    {
        Assert.Equal(2, ConcurrencyAdvisor.Pick(new[] { 1 * MB, 1 * MB }, 2));
        Assert.Equal(1, ConcurrencyAdvisor.Pick(new[] { 1 * MB }, 1));
        Assert.Equal(1, ConcurrencyAdvisor.Pick(System.Array.Empty<long>(), 0));
    }

    [Test]
    public void Unknown_sizes_count_as_small()
        => Assert.Equal(4, ConcurrencyAdvisor.Pick(Enumerable.Repeat(0L, 10).ToList(), 20));
}
