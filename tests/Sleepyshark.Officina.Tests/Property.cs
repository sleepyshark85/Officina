using CsCheck;

namespace Sleepyshark.Officina.Tests;

/// <summary>
/// Runs property tests on CsCheck with fixed seeds, so every run, CI's included, checks the same cases. A failing case is
/// then shrunk, and the failure reports the seed that reproduces it and the shrunk case.
/// </summary>
internal static class Property
{
    /// <summary>The seed every case derives from; change it locally to explore other cases.</summary>
    private const ulong Seed = 20_261_005;

    public static async Task CheckAsync<T>(Gen<T> gen, Func<T, Task> assert, Func<T, string> print, int cases = 200)
    {
        for (uint index = 0; index < cases; index++)
        {
            var seed = new PCG(index, Seed).ToString();
            try
            {
                await gen.SampleAsync(assert, seed: seed, iter: 1, threads: 1, print: print);
            }
            catch (CsCheckException)
            {
                // CsCheck starts from the failing seed and searches for smaller failing cases; its failure names the smallest.
                await gen.SampleAsync(assert, seed: seed, iter: 5_000, threads: 1, print: print);
                throw;
            }
        }
    }
}
