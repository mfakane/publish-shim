// Compatibility shim for C# record/init support when targeting .NET Standard 2.0.
// Newer target frameworks provide this type in the BCL.
#if NETSTANDARD2_0
namespace System.Runtime.CompilerServices;

internal static class IsExternalInit
{
}
#endif
