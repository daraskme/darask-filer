namespace Darask.App;

/// <summary>タブのコンテンツが実装する共通契約(docs/07 #24, #28)。フォルダータブとごみ箱タブなど、
/// タブ種別が増えても MainWindow 側はこのインターフェースだけを介して後片付けできるようにする。</summary>
public interface ITabContent
{
    /// <summary>タブが閉じられる時に呼ぶ(監視ハンドル・COM オブジェクトの解放)。</summary>
    void Shutdown();
}
