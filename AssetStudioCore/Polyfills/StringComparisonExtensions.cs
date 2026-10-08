#if NETFRAMEWORK
using System;
using System.Text;

namespace AssetStudioCore
{
    // .NET Framework's string has no Contains/Replace overloads that take a StringComparison.
    // Extension methods bind only when no instance overload applies, so modern targets never see these.
    internal static class StringComparisonExtensions
    {
        public static bool Contains(this string value, string search, StringComparison comparisonType)
        {
            return value.IndexOf(search, comparisonType) >= 0;
        }

        public static string Replace(this string value, string oldValue, string newValue, StringComparison comparisonType)
        {
            if (oldValue == null)
            {
                throw new ArgumentNullException(nameof(oldValue));
            }
            if (oldValue.Length == 0)
            {
                throw new ArgumentException("String cannot be of zero length.", nameof(oldValue));
            }

            var index = value.IndexOf(oldValue, comparisonType);
            if (index < 0)
            {
                return value;
            }

            var builder = new StringBuilder(value.Length);
            var start = 0;
            while (index >= 0)
            {
                builder.Append(value, start, index - start).Append(newValue);
                start = index + oldValue.Length;
                index = value.IndexOf(oldValue, start, comparisonType);
            }
            return builder.Append(value, start, value.Length - start).ToString();
        }
    }
}
#endif
