using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using CloudflareDdns.Gui.ViewModels;

namespace CloudflareDdns.Gui;

public partial class MainWindow : Window
{
    private MainViewModel? _vm;
    private bool _syncingToken;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm = (MainViewModel)DataContext;

        // The PasswordBox can't participate in data binding, so mirror it to the view model by hand.
        TokenBox.Password = _vm.ApiToken;
        TokenBox.PasswordChanged += (_, _) =>
        {
            if (_syncingToken) return;
            _syncingToken = true;
            _vm.ApiToken = TokenBox.Password;
            _syncingToken = false;
        };

        _vm.PropertyChanged += OnVmPropertyChanged;

        // Auto-scroll the live log to the newest entry.
        ((INotifyCollectionChanged)_vm.Logs).CollectionChanged += OnLogsChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.ApiToken) || _vm is null) return;
        if (_syncingToken) return;

        // Keep the hidden PasswordBox in step when the token is changed elsewhere
        // (loaded from disk, raw-JSON edit, or toggling Show off).
        if (TokenBox.Password != _vm.ApiToken)
        {
            _syncingToken = true;
            TokenBox.Password = _vm.ApiToken;
            _syncingToken = false;
        }
    }

    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || LogList.Items.Count == 0) return;
        LogList.ScrollIntoView(LogList.Items[^1]);
    }
}
