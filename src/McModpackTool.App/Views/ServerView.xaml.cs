using System.Windows.Controls;

namespace McModpackTool.App.Views;

public partial class ServerView : UserControl
{
    public ServerView()
    {
        InitializeComponent();
        DataContext = App.Localization;
    }
}
