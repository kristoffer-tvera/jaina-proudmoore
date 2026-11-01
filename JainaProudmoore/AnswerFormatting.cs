namespace JainaProudmoore;

/// <summary>
/// Pure, network-free text helpers for shaping Gemini answers before TTS/display.
/// </summary>
public static class AnswerFormatting
{
    /// <summary>
    /// Caps <paramref name="text"/> to at most <paramref name="maxSentences"/> sentences.
    /// This is an UPPER BOUND only: text with fewer terminators is returned unchanged
    /// (trimmed), and text with no terminator is treated as a single sentence. A run of
    /// terminators (for example <c>...</c> or <c>?!</c>) counts as one sentence end, and
    /// closing quotes/brackets immediately following a terminator are kept with that sentence.
    /// </summary>
    /// <remarks>
    /// Accepted trade-off: decimals and abbreviations (for example <c>3.14</c> or <c>Dr.</c>)
    /// contain a period that is counted as a sentence terminator, so such inputs may be cut
    /// shorter than a human would. This is acceptable for an upper-bound safety cap whose
    /// purpose is to prevent runaway output, not to perform perfect sentence segmentation.
    /// </remarks>
    public static string CapSentences(string text, int maxSentences = 3)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var trimmed = text.Trim();
        if (maxSentences <= 0)
        {
            return string.Empty;
        }

        var sentencesClosed = 0;
        var index = 0;
        while (index < trimmed.Length)
        {
            var c = trimmed[index];
            if (c is '.' or '!' or '?')
            {
                // Consume a run of terminators as a single sentence end.
                index++;
                while (index < trimmed.Length && trimmed[index] is '.' or '!' or '?')
                {
                    index++;
                }

                // Keep trailing closing quotes/brackets that belong to this sentence.
                while (
                    index < trimmed.Length
                    && trimmed[index] is '"' or '\'' or ')' or ']' or '}' or '\u201d' or '\u2019'
                )
                {
                    index++;
                }

                sentencesClosed++;
                if (sentencesClosed >= maxSentences)
                {
                    return trimmed[..index].Trim();
                }
            }
            else
            {
                index++;
            }
        }

        return trimmed;
    }
}
