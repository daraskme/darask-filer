namespace Darask.Service;

// M6 で --console フラグ付き昇格コンソール実行(FSCTL_ENUM_USN_DATA スイープ)を実装する。
// M9 で本物の Windows サービス化(LocalSystem、docs/02 §4/§7)する。
public static class Program
{
    public static int Main(string[] args)
    {
        Console.WriteLine("DaraskFilerd — placeholder (M6 で実装)");
        return 0;
    }
}
