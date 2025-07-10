
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Verse
{
    // TODO - implement multiple lookups for subject and any adjectives in it (with correct gender and number)
    // TODO - implement fix for vocalization in lookups (or we can use regex for it?)

    public class LanguageWorker_Czech : LanguageWorker
    {
        // constants
        private const int ReplaceRegexCacheSize = 107; // picked prime number from HashHelpers.cs
        private const int LookupCacheSize = 107; // picked prime number from HashHelpers.cs
        private static readonly string _lookupGenderFile = $"case{Path.DirectorySeparatorChar}gender";

        // logging
        private static readonly StringBuilder _log = new StringBuilder();
        private static bool _emitLog;

        // lookup cache
        private static readonly Dictionary<LookupCacheKey, string> _lookupCache = new Dictionary<LookupCacheKey, string>(LookupCacheSize);
        private static readonly LookupCacheKey[] _lookupKeys = new LookupCacheKey[LookupCacheSize];
        private static int _lookupKeyDeleteIndex;
        private static readonly char[] _fastLookupChars = new char[] { '(', '[' };

        // replace regex cache
        private static readonly Dictionary<string, Regex> _replaceRegexCache = new Dictionary<string, Regex>(ReplaceRegexCacheSize);
        private static readonly string[] _replaceRegexKeys = new string[ReplaceRegexCacheSize];
        private static int _replaceRegexKeyDeleteIndex;
        private static readonly char[] _firstPossibleRegexChars = new char[] { '(', '[', '{', '*', '+', '?', '.', '^', '$', '|' }; // only opening brackets are enough
        private static readonly Regex _replacePatternArgRegex = new Regex("(?<old>[^\"]*?)\"-\"(?<new>[^\"]*?)\"", RegexOptions.Compiled); // copied from base LanguageWorker

        /// <summary>
        /// Resolves a function call in the context of the Czech language, providing custom implementations for specific functions.
        /// </summary>
        /// <param name="functionName">The name of the function to resolve. Supported functions include <c>"lookup"</c> and <c>"replace"</c>.</param>
        /// <param name="args">A list of arguments passed to the function. The required arguments depend on the specific function being resolved.</param>
        /// <param name="fullStringForReference">The full string from which the function call originates, used for context or reference during resolution.</param>
        /// <returns>
        /// The result of the resolved function as a string. If the function is not recognized or cannot be resolved, the base implementation is used.
        /// </returns>
        /// <remarks>
        /// This method provides custom handling for the <c>"lookup"</c> and <c>"replace"</c> functions:
        /// <list type="bullet">
        /// <item>
        /// <description>
        /// The <c>"lookup"</c> function attempts to perform partial string lookups.
        /// </description>
        /// </item>
        /// <item>
        /// <description>
        /// The <c>"replace"</c> function applies substring or regex-based replacements to the input string.
        /// </description>
        /// </item>
        /// </list>
        /// If no custom handling is applicable, the base implementation of <see cref="LanguageWorker.ResolveFunction"/> is invoked.
        /// </remarks>
        public override string ResolveFunction(string functionName, List<string> args, string fullStringForReference)
        {
            try
            {
                if (functionName == "lookup"
                    && (args.Count == 3 || args.Count == 2))
                {
                    // for lookup function, we¨ll try our own implementation using partial string lookups
                    if (DebugSettings.logTranslationLookupErrors)
                    {
                        _log.Clear();
                        _log.AppendLine($"ResolveLookup - ({string.Join(", ", args)})");
                    }

                    return DoLookup(args, fullStringForReference);
                }
            }
            finally
            {
                // emit the log if we have any errors logged and we are in debug mode
                if (DebugSettings.logTranslationLookupErrors && _emitLog)
                {
                    Log.Message(_log.ToString());
                    _emitLog = false;
                    _log.Clear();
                }
            }


            if (functionName == "replace"
                && args.Count > 1)
            {
                // for replace function, we will try our own implementation using regex
                return DoReplace(args);
            }

            return base.ResolveFunction(functionName, args, fullStringForReference);
        }

        #region Lookup

        /// <summary>
        /// Performs a lookup operation based on the provided arguments and reference string.
        /// </summary>
        /// <param name="args">
        /// A list of arguments for the lookup operation. 
        /// The first argument is the original input string where we'll look for lookup, 
        /// the second argument is the path, 
        /// and the third (optional) argument specifies the index for the lookup defaults to <c>1</c>.
        /// </param>
        /// <param name="fullStringForReference">A reference string that provides additional context for the lookup operation.</param>
        /// <returns>The result of the lookup operation as a string. If the lookup fails, a fallback or default value may be returned.</returns>
        private string DoLookup(List<string> args, string fullStringForReference)
        {
            if (DebugSettings.logTranslationLookupErrors)
            {
                _emitLog = true;
            }

            // parse the arguments
            var originalInput = args[0];
            var path = args[1];

            // unify the path to use the system's directory separator character to match the environment's file system
            if (Path.DirectorySeparatorChar != '\\')
            {
                path = path.Replace('\\', Path.DirectorySeparatorChar);
                args[1] = path;
            }

            // get the index from the arguments, or default to 1 if not provided - same as in base game
            var index = args.Count == 3
                ? int.TryParse(args[2], out var parsedIndex) ? parsedIndex : -1
                : 1;

            // and construct the lookup cache key to speed up lookups
            var lookupCacheKey = new LookupCacheKey(originalInput, path, index);

            // if we have a cached result for this lookup, return it
            if (TryLookup(args, lookupCacheKey, out var lookupResult))
            {
                return lookupResult;
            }

            // try to find the first character that is possibly not a part of a lookupable string
            if (TryFindIndexOfAny(originalInput, _fastLookupChars, 0, out var fastLookupIndex, true))
            {
                // if we found a character that is not a part of a lookupable string, we can try to do a fast lookup using only the narrowed part of the string
                if (TryLookup(args, originalInput, 0, fastLookupIndex, out lookupResult))
                {
                    // if the fast lookup succeeded, save the result to the cache and return it
                    SaveToLookupCache(lookupCacheKey, lookupResult);
                    return lookupResult;
                }
            }
            else
            {
                // else reset lookup index to the whole string length again and do a full lookup again
                fastLookupIndex = 0;
            }

            // do the recursive lookup removing the last word from the input string
            if (TryLookupRecursive(args, originalInput, 0, fastLookupIndex == 0 ? originalInput.Length : fastLookupIndex, out lookupResult))
            {
                // if the recursive lookup succeeded, save the result to the cache and return it
                SaveToLookupCache(lookupCacheKey, lookupResult);
                return lookupResult;
            }

            // revert args[0] to original input, as it may have been modified in TryLookup and do base lookup but probably it will fail
            args[0] = originalInput;
            lookupResult = ResolveLookup(args, fullStringForReference);
            SaveToLookupCache(lookupCacheKey, lookupResult);
            return lookupResult;
        }

        /// <summary>
        /// Attempts to perform a lookup operation based on the provided arguments and cache key.
        /// </summary>
        /// <param name="args">A list of arguments used for the lookup operation.</param>
        /// <param name="lookupCacheKey">The unique key representing the lookup operation for caching purposes.</param>
        /// <param name="lookupResult">When this method returns, contains the result of the lookup operation if successful. Otherwise, an undefined value.</param>
        /// <returns><see langword="true"/> if the lookup operation succeeds and a result is found; otherwise, <see langword="false"/>.</returns>
        /// <remarks>
        /// This method first checks the lookup cache for a pre-existing result. If no cached result is found, 
        /// it attempts to perform the lookup operation and caches the result if successful.
        /// </remarks>
        private bool TryLookup(List<string> args, LookupCacheKey lookupCacheKey, out string lookupResult)
        {
            // check if we have a cached result for this lookup
            if (_lookupCache.TryGetValue(lookupCacheKey, out lookupResult))
            {
                if (DebugSettings.logTranslationLookupErrors)
                {
                    _log.AppendLine($" - Cache hit for {lookupCacheKey.Path} at index {lookupCacheKey.Index}: {lookupResult}");
                }

                _emitLog = false;
                return true;
            }

            // if we don't have a cached result, try to perform the lookup
            if (TryLookup(args, lookupCacheKey.OriginalInput, 0, lookupCacheKey.OriginalInput.Length, out lookupResult))
            {
                // and if the lookup succeeded, save the result to the cache
                SaveToLookupCache(lookupCacheKey, lookupResult);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Attempts to perform a recursive lookup by progressively removing the last word from the input string and trying to resolve it.
        /// </summary>
        /// <param name="args">The list of arguments used for the lookup process.</param>
        /// <param name="originalInput">The original input string to be processed.</param>
        /// <param name="fromIndex">The starting index in the input string for the lookup.</param>
        /// <param name="count">The number of characters to consider for the lookup starting from <paramref name="fromIndex"/>.</param>
        /// <param name="output">When this method returns, contains the result of the lookup if successful. Otherwise, <see langword="null"/>.</param>
        /// <returns><see langword="true"/> if the lookup is successful; otherwise, <see langword="false"/>.</returns>
        private bool TryLookupRecursive(List<string> args, string originalInput, int fromIndex, int count, out string output)
        {
            // check for invalid range
            if (fromIndex + count > originalInput.Length)
            {
                if (DebugSettings.logTranslationLookupErrors)
                {
                    _emitLog = true;
                    _log.AppendLine($"TryLookupRecursive - Invalid range {fromIndex + count} >= {originalInput.Length} (length of '{originalInput}'), returning false.");
                }

                output = null;
                return false;
            }

            var iteration = 0;

            // in loop try to recursively remove last word from input and try to lookup again
            while (true)
            {
                // 50 iterations could seem too much but the loop is expected to be short, so we can afford it
                if (iteration++ > 50)
                {
                    // if we have too many iterations, we assume that the input is too complex or the lookup is failing repeatedly or the code misbehaves
                    if (DebugSettings.logTranslationLookupErrors)
                    {
                        _emitLog = true;
                        _log.AppendLine($"TryLookupRecursive - Too many iterations ({iteration}), returning false.");
                    }

                    output = null;
                    return false;
                }

                // try to find the next character that (in backwards) is a whitespace to match a whole word
                var indexTo = fromIndex + count - 1;
                if (TryFindWhiteSpaceBackward(originalInput, ref indexTo, fromIndex, true))
                {
                    // trim any extra whitespace at the end and move to the end of the previous word
                    TryFindWhiteSpaceBackward(originalInput, ref indexTo, fromIndex, false);
                    count = indexTo - fromIndex;

                    // and do a lookup again without last word
                    if (!TryLookup(args, originalInput, fromIndex, count, out output)) continue;

                    // if lookup succeeded, we are done
                    return true;
                }

                // the lookup failed
                output = null;
                return false;
            }
        }

        /// <summary>
        /// Attempts to perform a lookup operation based on the provided arguments and input parameters.
        /// </summary>
        /// <param name="args">A list of arguments used for the lookup operation.</param>
        /// <param name="originalInput">The original input string to be used in the lookup process.</param>
        /// <param name="fromIndex">The starting index within the original input for the lookup.</param>
        /// <param name="count">The number of characters to consider for the lookup starting from <paramref name="fromIndex"/>.</param>
        /// <param name="output">When this method returns, contains the result of the lookup operation if successful. Otherwise, an empty string.</param>
        /// <returns><see langword="true"/> if the lookup operation was successful. Otherwise, <see langword="false"/>.</returns>
        private bool TryLookup(List<string> args, string originalInput, int fromIndex, int count, out string output)
        {
            if (DebugSettings.logTranslationLookupErrors)
            {
                _log.Append($"TryLookup: from:{fromIndex}, count:{count}");
            }

            output = "";
            if (count <= 0)
            {
                if (DebugSettings.logTranslationLookupErrors)
                {
                    _log.AppendLine(" - Invalid count, returning false.");
                }

                return false;
            }

            // if lookup is not whole string, we need to adjust args[0]
            if (fromIndex != 0 || count != originalInput.Length)
            {
                args[0] = originalInput.Substring(fromIndex, count);
            }

            // if lookup is whole string or an empty string, we consider it as a failure
            var lookupResult = ResolveLookup(args, originalInput);
            if (lookupResult == "" || lookupResult == args[0])
            {
                if (DebugSettings.logTranslationLookupErrors)
                {
                    _log.AppendLine($" - FAILED - args[0]: '{args[0]}'");
                }

                return false;
            }

            // for gender lookup we want to return only result without any prefix or suffix
            if (args[1] == _lookupGenderFile)
            {
                if (DebugSettings.logTranslationLookupErrors)
                {
                    _log.AppendLine($" - args[0]: '{args[0]}' - return: {output} (lookupResult: {lookupResult}, gender resolve)");
                }

                output = lookupResult;
                return true;
            }

            // if lookup succeeded, we need to reconstruct the output string using the prefix and suffix of the original input
            var hasPrefix = fromIndex > 0;
            var endLookupIndex = fromIndex + count;
            var hasSuffix = endLookupIndex < originalInput.Length;

            // construct the output string based on whether we have a prefix or suffix
            output = hasPrefix
                ? hasSuffix
                    ? $"{originalInput.Substring(0, fromIndex)}{lookupResult}{originalInput.Substring(endLookupIndex)}"
                    : $"{originalInput.Substring(0, fromIndex)}{lookupResult}"
                : hasSuffix
                    ? $"{lookupResult}{originalInput.Substring(endLookupIndex)}"
                    : lookupResult;
            if (DebugSettings.logTranslationLookupErrors)
            {
                _log.AppendLine($" - args[0]: '{args[0]}' - return: {output} (lookupResult: {lookupResult}, hasPrefix: {hasPrefix}, hasSuffix: {hasSuffix}, endLookupIndex: {endLookupIndex})");
            }

            return output != "";
        }

        private static void SaveToLookupCache(LookupCacheKey lookupCacheKey, string lookupResult)
        {
            SaveToCache(lookupCacheKey, lookupResult, _lookupCache, _lookupKeys, ref _lookupKeyDeleteIndex, LookupCacheSize);
        }

        #endregion

        #region Replace

        /// <summary>
        /// Replaces substrings in the input string based on specified replacement rules.
        /// </summary>
        /// <param name="args">
        /// A list of arguments where the first element is the input string, and subsequent elements define replacement rules.
        /// Each replacement rule should follow the format: <c>"oldValue"-"newValue"</c>.
        /// </param>
        /// <returns>
        /// The modified string after applying the replacement rules. If no replacements are made, the original string is returned.
        /// Returns <see langword="null"/> if the input arguments are invalid.
        /// </returns>
        /// <remarks>
        /// This method first attempts a simple substring replacement. If that fails, it interprets the <c>"oldValue"</c> as a regex pattern
        /// and applies it to the input string. If no replacements are successful, the original string is returned.
        /// </remarks>
        private string DoReplace(List<string> args)
        {
            // same as base for simple input
            if (args.Count == 0)
            {
                return null;
            }

            var str = args[0];
            if (args.Count == 1)
            {
                return str;
            }

            // use same loop asi in base game
            for (var index = 1; index < args.Count; ++index)
            {
                var argument = args[index];
                var argumentMatch = _replacePatternArgRegex.Match(argument);
                if (!argumentMatch.Success)
                {
                    return null;
                }

                var oldValueOrRegexPattern = argumentMatch.Groups["old"].Value;
                var newValueOrReplaceTemplate = argumentMatch.Groups["new"].Value;

                // first try simple replace
                if (str.Contains(oldValueOrRegexPattern))
                {
                    return str.Replace(oldValueOrRegexPattern, newValueOrReplaceTemplate);
                }

                // if simple replace didn't work, try parse as regex pattern
                var regex = GetReplaceRegex(oldValueOrRegexPattern);
                if (regex == null) continue;

                // if regex pattern is valid, try to apply it
                var replaced = regex.Replace(str, newValueOrReplaceTemplate);
                if (replaced == str) continue; // but if it didn't change, continue to next argument
                return replaced;
            }
            return str;
        }

        /// <summary>
        /// Retrieves a compiled <see cref="Regex"/> object for the given pattern, utilizing a cache to improve performance.
        /// </summary>
        /// <param name="regexPattern">The regex pattern to compile and retrieve.</param>
        /// <returns>A compiled <see cref="Regex"/> object if the pattern is valid and contains special regex characters, otherwise, <see langword="null"/>.</returns>  
        /// <remarks>
        /// If the pattern is invalid or does not contain any special regex characters, the method caches the result as <see langword="null"/> 
        /// to avoid repeated compilation attempts.
        /// </remarks>
        private Regex GetReplaceRegex(string regexPattern)
        {
            // first check cache and return result if found
            if (_replaceRegexCache.TryGetValue(regexPattern, out var regex)) return regex;

            // check if the pattern contains any of the special regex pattern characters, if not, the regex will be meaningless
            if (!TryFindIndexOfAny(regexPattern, _firstPossibleRegexChars, 0, out _))
            {
                SaveToReplaceCache(regexPattern, null);
                return null;
            }

            try
            {
                // try to compile the regex pattern
                regex = new Regex(regexPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);

                // and save it to the cache
                SaveToReplaceCache(regexPattern, regex);
                return regex;
            }
            catch
            {
                // if the regex pattern is invalid, save the result to cache as well so we don't try to compile it again
                SaveToReplaceCache(regexPattern, null);
                return null;
            }
        }

        /// <summary>
        /// Stores a compiled regular expression or a <see langword="null"/> value in the replace regex cache.
        /// If the cache reaches its maximum size, the oldest entry is removed to make space for the new entry.
        /// </summary>
        /// <param name="replacePattern">The regex pattern to be used as the key in the cache.</param>
        /// <param name="regex">The compiled <see cref="Regex"/> object to be stored in the cache, or <see langword="null"/> if the pattern is invalid.</param>
        private static void SaveToReplaceCache(string replacePattern, Regex regex)
        {
            SaveToCache(replacePattern, regex, _replaceRegexCache, _replaceRegexKeys, ref _replaceRegexKeyDeleteIndex, ReplaceRegexCacheSize);
        }

        #endregion

        /// <summary>
        /// Attempts to find the index of any character from the specified array within the input string, starting from a given index.
        /// </summary>
        /// <param name="input">The input string to search within.</param>
        /// <param name="chars">An array of characters to search for.</param>
        /// <param name="fromIndex">The index in the input string to start the search from.</param>
        /// <param name="index">When this method returns, contains the index of the first occurrence of any character from the array, or -1 if no such character is found.</param>
        /// <param name="trim">A boolean value indicating whether to skip whitespace characters when searching.</param>
        /// <returns>
        /// <see langword="true"/> if any character from the array is found in the input string; otherwise, <see langword="false"/>.
        /// </returns>
        private static bool TryFindIndexOfAny(string input, char[] chars, int fromIndex, out int index, bool trim = false)
        {
            index = input.IndexOfAny(chars, fromIndex);
            if (index == -1)
            {
                return false;
            }

            if (trim)
            {
                // skip any whitespace characters if trim is specified
                TryFindWhiteSpaceBackward(input, ref index, fromIndex, false);
            }
            return true;
        }

        /// <summary>
        /// Attempts to find a whitespace character or a non-whitespace character (based on the <paramref name="lookForWhitespace"/>)
        /// by moving backward through the given string starting from the specified index.
        /// </summary>
        /// <param name="input">The input string to search within.</param>
        /// <param name="index">A reference to the current index in the string. This value will be updated to the position of the found character if the search is successful.</param>
        /// <param name="stopIndex">The index at which the search should stop. The search will not proceed past this index.</param>
        /// <param name="lookForWhitespace">A boolean value indicating the type of character to search for: <see langword="true"/> to search for whitespace characters; <see langword="false"/> to search for non-whitespace characters.</param>
        /// <returns><see langword="true"/> if a character matching the specified condition is found; otherwise, <see langword="false"/>.</returns>
        private static bool TryFindWhiteSpaceBackward(string input, ref int index, int stopIndex, bool lookForWhitespace)
        {
            while (true)
            {
                if (index <= 0) return false; // if index is already at the start, return false
                if (index <= stopIndex) return false;
                if (char.IsWhiteSpace(input[index - 1]) == lookForWhitespace) return true;
                index--;
            }
        }

        /// <summary>
        /// Saves a key-value pair to the specified cache, ensuring that the cache size does not exceed the defined limit.
        /// If the cache is full, the oldest entry is removed to make space for the new entry.
        /// </summary>
        /// <typeparam name="TKey">The type of the key used in the cache.</typeparam>
        /// <typeparam name="TValue">The type of the value stored in the cache.</typeparam>
        /// <param name="key">The key to associate with the value in the cache.</param>
        /// <param name="value">The value to store in the cache.</param>
        /// <param name="cache">The dictionary representing the cache.</param>
        /// <param name="keys">An array maintaining the order of keys in the cache for eviction purposes.</param>
        /// <param name="deleteIndex">
        /// A reference to the index indicating the next key to be removed when the cache is full.
        /// This index (<paramref name="deleteIndex"/>) is updated as entries are added or replaced.
        /// </param>
        /// <param name="cacheSize">The maximum size of the cache.</param>
        private static void SaveToCache<TKey, TValue>(TKey key, TValue value, Dictionary<TKey, TValue> cache, TKey[] keys, ref int deleteIndex, int cacheSize)
        {
            if (cache.Count == cacheSize)
            {
                // if cache is full, remove the oldest entry
                var keyToRemove = keys[deleteIndex];
                cache.Remove(keyToRemove);
                keys[deleteIndex] = key;

                // update the delete index to point to the next key to be removed in a circular manner
                deleteIndex = (deleteIndex + 1) % cacheSize;
            }
            else
            {
                // if cache is not full, just add the new key
                keys[cache.Count] = key;
            }

            cache[key] = value;
        }

        /// <summary>
        /// Represents a unique key used for caching lookup results.
        /// </summary>
        /// <remarks>
        /// This struct is designed to provide efficient equality checks and hashing for caching purposes.
        /// It combines the original input string, a file path, and an index to form a unique identifier.
        /// </remarks>
        private readonly struct LookupCacheKey : IEquatable<LookupCacheKey>
        {
            /// <summary>
            /// Gets the original input string used to create this lookup cache key.
            /// </summary>
            public string OriginalInput { get; }

            /// <summary>
            /// Gets the file path associated with this lookup cache key.
            /// </summary>
            /// <remarks>
            /// The path is unified to use <c>'\'</c> as the system's directory separator character,
            /// </remarks>
            public string Path { get; }

            /// <summary>
            /// Index in lookup table for this key.
            /// </summary>
            public int Index { get; }

            public LookupCacheKey(string originalInput, string path, int index)
            {
                OriginalInput = originalInput;
                Path = path;
                Index = index;
            }

            private bool Equals(LookupCacheKey other)
            {
                return Index == other.Index &&
                       string.Equals(Path, other.Path, StringComparison.Ordinal) &&
                       string.Equals(OriginalInput, other.OriginalInput, StringComparison.Ordinal);
            }

            public override bool Equals(object obj)
            {
                return obj is LookupCacheKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = Index;
                    hash = (hash * 397) ^ (Path?.GetHashCode() ?? 0);
                    hash = (hash * 397) ^ (OriginalInput?.GetHashCode() ?? 0);
                    return hash;
                }
            }

            bool IEquatable<LookupCacheKey>.Equals(LookupCacheKey other)
            {
                return Equals(other);
            }
        }
    }
}
