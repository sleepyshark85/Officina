using System.Text;

namespace WordCount;

public readonly record struct Counts(long Lines, long Words, long Bytes)
{
    public static Counts Of(byte[] content)
    {
        long lines = 0, words = 0;
        var inWord = false;
        foreach (var character in Encoding.UTF8.GetString(content))
        {
            if (character == '\n')
            {
                lines++;
            }

            var space = char.IsWhiteSpace(character);
            if (!space && !inWord)
            {
                words++;
            }

            inWord = !space;
        }

        return new(lines, words, content.LongLength);
    }

    public static Counts operator +(Counts a, Counts b) => new(a.Lines + b.Lines, a.Words + b.Words, a.Bytes + b.Bytes);

    public string Format(bool lines, bool words, bool bytes)
    {
        var parts = new List<long>();
        if (lines) parts.Add(Lines);
        if (words) parts.Add(Words);
        if (bytes) parts.Add(Bytes);
        return string.Join(' ', parts);
    }
}

public static class Program
{
    public static int Main(string[] args)
    {
        bool lines = false, words = false, bytes = false;
        var paths = new List<string>();
        foreach (var arg in args)
        {
            if (arg.Length > 1 && arg[0] == '-')
            {
                foreach (var option in arg[1..])
                {
                    switch (option)
                    {
                        case 'l': lines = true; break;
                        case 'w': words = true; break;
                        case 'c': bytes = true; break;
                        default:
                            Console.Error.WriteLine($"wc: unknown option -{option}");
                            return 2;
                    }
                }
            }
            else
            {
                paths.Add(arg);
            }
        }

        if (!lines && !words && !bytes)
        {
            lines = words = bytes = true;
        }

        if (paths.Count == 0)
        {
            using var input = new MemoryStream();
            Console.OpenStandardInput().CopyTo(input);
            Console.WriteLine(Counts.Of(input.ToArray()).Format(lines, words, bytes));
            return 0;
        }

        var exit = 0;
        var total = new Counts();
        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"wc: {path}: No such file");
                exit = 1;
                continue;
            }

            var counts = Counts.Of(File.ReadAllBytes(path));
            total += counts;
            Console.WriteLine($"{counts.Format(lines, words, bytes)} {path}");
        }

        if (paths.Count > 1)
        {
            Console.WriteLine($"{total.Format(lines, words, bytes)} total");
        }

        return exit;
    }
}
