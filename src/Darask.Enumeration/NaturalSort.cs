using Windows.Win32;
using Windows.Win32.Foundation;

namespace Darask.Enumeration;

/// <summary>
/// StrCmpLogicalW ベースの自然順比較(Explorer と同一の並び順、日本語コラレーション含む。
/// docs/05 §10, CLAUDE.md)。
/// </summary>
public static class NaturalSort
{
    public static unsafe int Compare(string a, string b)
    {
        fixed (char* pa = a)
        fixed (char* pb = b)
        {
            return PInvoke.StrCmpLogical((PCWSTR)pa, (PCWSTR)pb);
        }
    }

    public static readonly IComparer<string> StringComparer = System.Collections.Generic.Comparer<string>.Create(Compare);
}
