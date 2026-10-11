namespace BookshopAssistant;

/// <summary>Where the console reads and writes; <paramref name="EchoInput"/> writes each line read, for redirected input.</summary>
public sealed record Terminal(TextReader Input, TextWriter Output, bool EchoInput);
