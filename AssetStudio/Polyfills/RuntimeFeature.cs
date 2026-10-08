#if NETFRAMEWORK
// .NET Framework's System.Runtime.CompilerServices.RuntimeFeature has no IsDynamicCodeSupported.
// Code in namespace AssetStudio resolves RuntimeFeature to this type before the imported
// System one, so the NativeAOT guards compile unchanged for net472, where dynamic code is
// always available. Modern targets keep using the framework's feature switch, which the
// trimmer and NativeAOT compiler fold to a constant. AssetStudioUtility links this file too.
namespace AssetStudio
{
    internal static class RuntimeFeature
    {
        public const bool IsDynamicCodeSupported = true;
    }
}
#endif
