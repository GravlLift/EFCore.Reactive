namespace System
{
    public static class TypeExtensions
    {
        public static Type GetUnderlyingNullableOrType(this Type type) =>
            Nullable.GetUnderlyingType(type) ?? type;
    }
}
