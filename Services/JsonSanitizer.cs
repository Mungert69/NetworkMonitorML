using System;
using System.Text.RegularExpressions;
namespace NetworkMonitor.ML.Services;
public class JsonSanitizer
{
    public static string SanitizeJson(string input)
    {
        // Step 1: Remove single quotes around JSON objects and arrays
        // This regex looks for single quotes around {..} and [..], considering nested structures
        string patternObjectsAndArrays = @"'(\{(?:[^{}]|(?<open>\{)|(?<-open>\}))*\})'|'\[(?:[^\[\]]|(?<open>\[)|(?<-open>\]))*\]'";
        string sanitized = Regex.Replace(input, patternObjectsAndArrays, m => 
        {
            // Replace single quotes with nothing, effectively removing them
            return m.Groups[1].Value;
        }, RegexOptions.Singleline | RegexOptions.IgnorePatternWhitespace);

        // Step 2: Replace all remaining single quotes with double quotes
        // This step avoids changing single quotes that are part of a double-quoted string
        sanitized = Regex.Replace(sanitized, @"(?<!\\)'", "\"");

         // Step 3: Correct boolean values
    // Replace "True" and "False" with "true" and "false"
    sanitized = Regex.Replace(sanitized, @"\bTrue\b", "true", RegexOptions.IgnoreCase);
    sanitized = Regex.Replace(sanitized, @"\bFalse\b", "false", RegexOptions.IgnoreCase);


        return sanitized;
    }
}
