// Required to support C# 9 'init' accessors and records on .NET Standard 2.0 / .NET Framework targets.
// Linked (not copied) into each such project via <Compile Include="...build\IsExternalInit.cs" />.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
