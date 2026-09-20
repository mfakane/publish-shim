using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PublishShim.WinUI3.Sample;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        Content = new Grid
        {
            Children =
            {
                new TextBlock
                {
                    Text = "PublishShim WinUI 3 smoke sample",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
    }
}
