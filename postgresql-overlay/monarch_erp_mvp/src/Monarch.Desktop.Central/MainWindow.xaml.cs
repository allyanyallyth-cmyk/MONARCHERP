using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Monarch.Core.Abstractions;
using Monarch.Core.Remote;
using Monarch.Desktop.Authentication;

namespace Monarch.Desktop.Central;

public partial class MainWindow : Window
{
    private readonly CentralDesktopConfiguration? _configuration;
    private readonly MsalAccessTokenProvider? _tokens;
    private readonly HttpClient? _tenantHttp;
    private readonly HttpClient? _ledgerHttp;
    private readonly ITenantDiscoveryClient? _tenantDiscovery;
    private readonly IGeneralLedgerStore? _ledger;
    private bool _changingSelection;

    public MainWindow()
    {
        InitializeComponent();
        try
        {
            _configuration = CentralDesktopConfiguration.LoadFromEnvironment();
            _tokens = new MsalAccessTokenProvider(_configuration.Authentication);
            _tenantHttp = CreateHttpClient(new Uri(_configuration.ApiBaseAddress, "api/v1/tenant-context/"));
            _ledgerHttp = CreateHttpClient(new Uri(_configuration.ApiBaseAddress, "api/v1/general-ledger/"));
            _tenantDiscovery = new HttpTenantDiscoveryClient(_tenantHttp, _tokens);
            _ledger = new HttpGeneralLedgerStore(_ledgerHttp, _tokens);
            StatusText.Text = "Configuration validated. Sign in to discover authorized companies and branches.";
        }
        catch (Exception exception)
        {
            SignInButton.IsEnabled = false;
            StatusText.Text = "Central client configuration error: " + exception.Message;
        }
    }

    private static HttpClient CreateHttpClient(Uri baseAddress) => new()
    {
        BaseAddress = baseAddress,
        Timeout = TimeSpan.FromSeconds(30)
    };

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(async () =>
        {
            _ = await _tokens!.GetAccessTokenAsync();
            var companies = await _tenantDiscovery!.ListCompaniesAsync();
            _changingSelection = true;
            CompanyBox.ItemsSource = companies;
            CompanyBox.DisplayMemberPath = nameof(TenantCompany.LegalName);
            CompanyBox.SelectedValuePath = nameof(TenantCompany.CompanyId);
            CompanyBox.SelectedIndex = -1;
            BranchBox.ItemsSource = null;
            BranchBox.IsEnabled = false;
            CompanyBox.IsEnabled = true;
            SignOutButton.IsEnabled = true;
            AccountsGrid.ItemsSource = null;
            _changingSelection = false;
            StatusText.Text = companies.Count == 0
                ? "Signed in. The identity has no active company grants."
                : $"Signed in. {companies.Count} authorized company record(s) discovered.";
        });
    }

    private async void Company_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_changingSelection || CompanyBox.SelectedItem is not TenantCompany company) return;
        await RunBusyAsync(async () =>
        {
            _changingSelection = true;
            BranchBox.ItemsSource = null;
            BranchBox.IsEnabled = false;
            AccountsGrid.ItemsSource = null;
            _changingSelection = false;
            var branches = await _tenantDiscovery!.ListBranchesAsync(company.CompanyId);
            _changingSelection = true;
            BranchBox.ItemsSource = branches;
            BranchBox.DisplayMemberPath = nameof(TenantBranch.BranchName);
            BranchBox.SelectedValuePath = nameof(TenantBranch.BranchId);
            BranchBox.SelectedIndex = -1;
            BranchBox.IsEnabled = branches.Count > 0;
            _changingSelection = false;
            StatusText.Text = $"Company {company.CompanyCode}: {branches.Count} authorized branch record(s) discovered.";
        });
    }

    private async void Branch_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_changingSelection || CompanyBox.SelectedItem is not TenantCompany company ||
            BranchBox.SelectedItem is not TenantBranch branch) return;
        await RunBusyAsync(async () =>
        {
            var tenant = new TenantScope(company.CompanyId, branch.BranchId, Guid.Empty);
            var accounts = await _ledger!.GetAccountsAsync(tenant, includeInactive: false);
            AccountsGrid.ItemsSource = accounts;
            StatusText.Text = $"Connected to central PostgreSQL API. {accounts.Count} account record(s) loaded for {company.CompanyCode}/{branch.BranchCode}. Read-only validation slice.";
        });
    }

    private async void SignOut_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(async () =>
        {
            await _tokens!.SignOutAsync();
            _changingSelection = true;
            CompanyBox.ItemsSource = null;
            BranchBox.ItemsSource = null;
            CompanyBox.IsEnabled = false;
            BranchBox.IsEnabled = false;
            AccountsGrid.ItemsSource = null;
            SignOutButton.IsEnabled = false;
            _changingSelection = false;
            StatusText.Text = "Signed out. No local database fallback is available in this client.";
        });
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        SignInButton.IsEnabled = false;
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            StatusText.Text = "Central operation failed: " + exception.Message + " No local fallback was attempted.";
        }
        finally
        {
            SignInButton.IsEnabled = _tokens is not null;
        }
    }

    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_tokens is not null) await _tokens.DisposeAsync();
        _tenantHttp?.Dispose();
        _ledgerHttp?.Dispose();
    }
}
