// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT License.

using MudSeverity = MudBlazor.Severity;

namespace Orleans.ShoppingCart.Silo.Shared;

public partial class MainLayout
{
    const string PrefersDarkThemeKey = "prefers-dark-scheme";
    
    readonly MudTheme _theme = new()
    {
        Palette = new Palette()
        {
            Tertiary = "#7e6fff",
            DrawerIcon = "#aaa9b9",
            DrawerText = "#aaa9b9",
            DrawerBackground = "#303030"
        },
        PaletteDark = new Palette()
        {
            Primary = "#7e6fff",
            Tertiary = "#7e6fff",
            Surface = "#1e1e2d",
            Background = "#1a1a27",
            BackgroundGrey = "#151521",
            AppbarText = "#92929f",
            AppbarBackground = "rgba(26,26,39,0.8)",
            DrawerBackground = "#1a1a27",
            ActionDefault = "#74718e",
            ActionDisabled = "#9999994d",
            ActionDisabledBackground = "#605f6d4d",
            TextPrimary = "#b2b0bf",
            TextSecondary = "#92929f",
            TextDisabled = "#ffffff33",
            DrawerIcon = "#92929f",
            DrawerText = "#92929f",
            GrayLight = "#2a2833",
            GrayLighter = "#1e1e2d",
            Info = "#4a86ff",
            Success = "#3dcb6c",
            Warning = "#ffb545",
            Error = "#ff3f5f",
            LinesDefault = "#33323e",
            TableLines = "#33323e",
            Divider = "#292838",
            OverlayLight = "#1e1e2d80"
        },
    };

    bool _drawerOpen = true;
    bool _isDarkTheme;

    [Inject]
    public ToastService ToastService { get; set; } = null!;

    [Inject]
    public ISnackbar Snackbar { get; set; } = null!;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            ToastService.OnToastedRequested += OnToastRequested;

            if (await LocalStorage.GetItemAsync(PrefersDarkThemeKey)
                is { Length: > 0 } isDarkTheme &&
                bool.TryParse(isDarkTheme, out var parsedValue))
            {
                _isDarkTheme = parsedValue;
            }

            await base.OnAfterRenderAsync(firstRender);
        }
    }

    async Task OnToggledChangedAsync(bool value) =>
        await LocalStorage.SetItemAsync(
            PrefersDarkThemeKey, (_isDarkTheme = value).ToString());

    Task OnToastRequested((string Title, string Message) tuple) =>
        InvokeAsync(() =>
        {
            var (_, message) = tuple;

            Snackbar.Add(
                message, 
                MudSeverity.Success,
                options => options.CloseAfterNavigation = true);
        });

    void DrawerToggle() => _drawerOpen = !_drawerOpen;
}
