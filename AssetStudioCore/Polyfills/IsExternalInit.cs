#if NETFRAMEWORK
// Lets `record` and `init` accessors compile for net472; .NET 5+ ships this type.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
#endif
