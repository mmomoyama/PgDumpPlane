namespace PgDumpPlane.WinForms;

/// <summary>
/// <para>Windows Formsサンプルのエントリーポイントです。</para>
/// <para>Contains the entry point for the Windows Forms sample.</para>
/// </summary>
internal static class Program
{
    /// <summary>
    /// <para>Windows FormsをSTAスレッドで初期化し、メイン画面を起動します。</para>
    /// <para>Initializes Windows Forms on an STA thread and launches the main window.</para>
    /// </summary>
    // Windows FormsとファイルダイアログのCOM連携に必要なSTAスレッドを指定します。
    // Use an STA thread for Windows Forms and file-dialog COM integration.
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
