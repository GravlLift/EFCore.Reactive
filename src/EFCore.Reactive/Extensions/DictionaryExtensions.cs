namespace System.Collections.Generic
{
    static class DictionaryExtensions
    {
        /// <summary>
        /// If a key exists in the dictionary, the corresponding value is returned.
        /// If the key is not found, the <paramref name="addFunction"/> function is
        /// executed and the result is added to the dictionary using
        /// <paramref name="key"/>.
        public static TValue GetOrAdd<TKey, TValue>(
            this IDictionary<TKey, TValue> dict,
            TKey key,
            Func<TValue> addFunction
        )
        {
            if (!dict.TryGetValue(key, out var val))
            {
                val = addFunction();
                dict[key] = val;
            }

            return val;
        }
    }
}
