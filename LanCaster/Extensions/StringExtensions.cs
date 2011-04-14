using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace System
{
    public static class StringExtensions
    {
        private static char[] wrapChars = new char[] { ' ', '\t', '\r', '\n', '-' };

        /// <summary>
        /// Word-wrap a string of text to separate lines broken at the nth column.
        /// </summary>
        /// <param name="text"></param>
        /// <param name="columns">Maximum number of columns per line</param>
        /// <returns>An enumerable implementation yielding each line</returns>
        public static IEnumerable<string> WordWrap(this string text, int columns)
        {
            string line;
            int i = 0;
            int remainingLength = text.Length;
            if (remainingLength == 0)
            {
                yield return text;
                yield break;
            }

            // Keep chopping lines until there's not enough text to chop:
            while (remainingLength > columns)
            {
                // Find the last occurrence of a word-wrap character in this line:
                int lastSplitCharIdx = text.LastIndexOfAny(wrapChars, i + columns, columns);
                if (lastSplitCharIdx < 0)
                {
                    // One long uninterruptible line, force a break at the `columns` mark.
                    lastSplitCharIdx = i + columns;
                }

                // Minimum line length threshold at 50%:
                int lineLength = lastSplitCharIdx - i;
                if (lineLength < columns / 2)
                {
                    lineLength = columns;
                }

                // Pick out the text up to the word-wrap character:
                line = text.Substring(i, lineLength).TrimEnd();
                yield return line;

                // Set up for the next chop:
                i += lineLength;
                while (i < text.Length && Char.IsWhiteSpace(text[i])) ++i;
                remainingLength = text.Length - i;
            }

            // Yield the last portion of the string:
            if (remainingLength > 0)
            {
                line = text.Substring(i).TrimEnd();
                yield return line;
            }

            // Done.
            yield break;
        }

        public static string NullTrim(this string value)
        {
            if (value == null) return null;
            return value.Trim();
        }

        public static int ToInt32Or(this string value, int defaultValue)
        {
            if (value == null) return defaultValue;

            int intValue;
            if (Int32.TryParse(value, out intValue)) return intValue;
            return defaultValue;
        }

        public static int? ToInt32OrNull(this string value)
        {
            if (value == null) return null;

            int intValue;
            if (Int32.TryParse(value, out intValue)) return intValue;
            return null;
        }
    }
}
