using CsCheck;

namespace Sleepyshark.Officina.Tests;

/// <summary>
/// Runs property tests on CsCheck with fixed seeds, so every run, CI's included, checks the same cases. A failing case is
/// then shrunk, and the failure reports the seed of the shrunk case and the case itself.
/// </summary>
/// <remarks>
/// To replay exactly one case, set the environment variable <c>OFFICINA_SEED</c> to the seed a failure reports and run
/// the test: only that case runs. CsCheck's own <c>CsCheck_Seed</c> hint does not apply here, as each case passes its
/// seed explicitly.
/// </remarks>
internal static class Property
{
    /// <summary>The seed every case derives from; change it locally to explore other cases.</summary>
    private const ulong Seed = 20_261_005;

    public static async Task CheckAsync<T>(Gen<T> gen, Func<T, Task> assert, Func<T, string> print, int cases = 200)
    {
        if (Environment.GetEnvironmentVariable("OFFICINA_SEED") is { Length: > 0 } replay)
        {
            await gen.SampleAsync(assert, seed: replay, iter: 1, threads: 1, print: print);
            return;
        }

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
                try
                {
                    await gen.SampleAsync(assert, seed: seed, iter: 5_000, threads: 1, print: print);
                }
                catch (CsCheckException shrunk)
                {
                    throw new InvalidOperationException(
                        $"{shrunk.Message}\nTo replay exactly this case, set OFFICINA_SEED to the seed above (not CsCheck_Seed).", shrunk);
                }

                throw;
            }
        }
    }
}
