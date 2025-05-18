using Avalonia.Controls;
// using Avalonia.Interactivity; // No longer needed for VisualTreeAttachmentEventArgs
// using Avalonia; // No longer needed for Bounds
// using MyApp.ViewModels; // No longer needed for ViewModel interaction here
// using System; // No longer needed

namespace MyApp.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    // OnAttachedToVisualTree and OnDetachedFromVisualTree for SizeChanged are no longer needed
    // protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e) 
    // {
    //     base.OnAttachedToVisualTree(e);
    //     this.SizeChanged += MainWindow_SizeChanged;
    //     if (this.DataContext is MainWindowViewModel vm)
    //     {
    //         vm.SetWindowWidthCommand?.Execute(this.Bounds.Width);
    //     }
    // }

    // protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    // {
    //     base.OnDetachedFromVisualTree(e);
    //     this.SizeChanged -= MainWindow_SizeChanged;
    // }

    // MainWindow_SizeChanged is no longer needed
    // private void MainWindow_SizeChanged(object? sender, SizeChangedEventArgs e)
    // {
    //     if (this.DataContext is MainWindowViewModel vm)
    //     {
    //         vm.SetWindowWidthCommand?.Execute(e.NewSize.Width);
    //     }
    // }
}