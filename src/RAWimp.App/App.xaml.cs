// RAWimp — a fast photo browser, culler and image importer for Windows.
// Copyright (C) 2026 Stefan Bauckmeier
//
// This program is free software: you can redistribute it and/or modify it under the terms of the GNU
// General Public License as published by the Free Software Foundation, either version 3 of the
// License, or (at your option) any later version. It is distributed in the hope that it will be
// useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS
// FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License along with this program. If not,
// see <https://www.gnu.org/licenses/>. The full text is in LICENSE at the root of the repository.
//
// The name "RAWimp" and the imp artwork are not covered by that licence — see assets/LICENSE.md.

using Microsoft.UI.Xaml;

namespace RAWimp.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        Diag.Log("App ctor");
        UnhandledException += (_, e) => Diag.Log("UNHANDLED (xaml): " + e.Message + "\n" + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Diag.Log("UNHANDLED (domain): " + (e.ExceptionObject as Exception));
        TaskScheduler.UnobservedTaskException += (_, e) => { Diag.Log("UNOBSERVED TASK: " + e.Exception); e.SetObserved(); };
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Diag.Log("OnLaunched");
        _window = new MainWindow();
        _window.Closed += (_, _) => Diag.Log("window Closed");
        _window.Activate();
        Diag.Log("window Activated");
    }
}
