using EmvCardReader.ViewModels;

namespace EmvCardReader;

public partial class MainPage : ContentPage
{
    public MainPage() : this(ServiceHelper.GetService<MainViewModel>()
        ?? throw new InvalidOperationException($"{nameof(MainViewModel)} is not registered."))
    {
    }

    public MainPage(MainViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
