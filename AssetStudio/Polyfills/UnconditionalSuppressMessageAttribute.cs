#if !NET5_0_OR_GREATER
// .NET Framework has no public UnconditionalSuppressMessageAttribute; the copy shipped
// inside the System.Text.Json package is internal (CS0122). The trimming and NativeAOT
// analyzers match the attribute by full name, so this internal copy only lets the
// shared sources compile for net472. .NET 5+ targets use the framework's attribute.
namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.All, Inherited = false, AllowMultiple = true)]
    internal sealed class UnconditionalSuppressMessageAttribute : Attribute
    {
        public UnconditionalSuppressMessageAttribute(string category, string checkId)
        {
            Category = category;
            CheckId = checkId;
        }

        public string Category { get; }

        public string CheckId { get; }

        public string Scope { get; set; }

        public string Target { get; set; }

        public string MessageId { get; set; }

        public string Justification { get; set; }
    }
}
#endif
