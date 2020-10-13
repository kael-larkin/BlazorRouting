﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Runtime.ExceptionServices;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Components.Routing;

namespace BlazorRouting
{
    public class Router : IComponent, IHandleAfterRender, IDisposable
    {
        static readonly char[] _queryOrHashStartChar = new[] { '?', '#' };
        static readonly Dictionary<string, object?> _emptyParametersDictionary
            = new Dictionary<string, object?>(StringComparer.Ordinal);

        RenderHandle? _renderHandle;
        string? _baseUri;
        string? _locationAbsolute;
        bool _navigationInterceptionEnabled = false;
        ILogger<Router>? _logger;

        private CancellationTokenSource? _onNavigateCts;

        private Task _previousOnNavigateTask = Task.CompletedTask;

        private readonly HashSet<Assembly> _assemblies = new HashSet<Assembly>();

        private bool _onNavigateCalled = false;

        private Type? _lastRender = null;

        [Inject] private NavigationManager? NavigationManager { get; set; }

        [Inject] private INavigationInterception? NavigationInterception { get; set; }

        [Inject] private ILoggerFactory? LoggerFactory { get; set; }

        [Inject] private RouteContainer<Type>? RouteContainer { get; set; }

        /// <summary>
        /// Gets or sets the assembly that should be searched for components matching the URI.
        /// </summary>
        [Parameter] public Assembly? AppAssembly { get; set; }

        /// <summary>
        /// Gets or sets a collection of additional assemblies that should be searched for components
        /// that can match URIs.
        /// </summary>
        [Parameter] public IEnumerable<Assembly>? AdditionalAssemblies { get; set; }

        /// <summary>
        /// Gets or sets the content to display when no match is found for the requested route.
        /// </summary>
        [Parameter] public RenderFragment? NotFound { get; set; }

        /// <summary>
        /// Gets or sets the content to display when a match is found for the requested route.
        /// </summary>
        [Parameter] public RenderFragment<RouteData>? Found { get; set; }

        /// <summary>
        /// Get or sets the content to display when asynchronous navigation is in progress.
        /// </summary>
        [Parameter] public RenderFragment? Navigating { get; set; }

        /// <summary>
        /// Gets or sets a handler that should be called before navigating to a new page.
        /// </summary>
        [Parameter] public Func<NavigationContext, Task>? OnNavigateAsync { get; set; }

        /// <inheritdoc />
        public void Attach(RenderHandle renderHandle)
        {
            _logger = LoggerFactory.CreateLogger<Router>();
            _renderHandle = renderHandle;
            _baseUri = NavigationManager?.BaseUri;
            _locationAbsolute = NavigationManager!.Uri;
            NavigationManager.LocationChanged += OnLocationChanged;
        }

        /// <inheritdoc />
        public async Task SetParametersAsync(ParameterView parameters)
        {
            parameters.SetParameterProperties(this);

            if (AppAssembly == null)
            {
                throw new InvalidOperationException($"The {nameof(Router)} component requires a value for the parameter {nameof(AppAssembly)}.");
            }

            // Found content is mandatory, because even though we could use something like <RouteView ...> as a
            // reasonable default, if it's not declared explicitly in the template then people will have no way
            // to discover how to customize this (e.g., to add authorization).
            if (Found == null)
            {
                throw new InvalidOperationException($"The {nameof(Router)} component requires a value for the parameter {nameof(Found)}.");
            }

            // NotFound content is mandatory, because even though we could display a default message like "Not found",
            // it has to be specified explicitly so that it can also be wrapped in a specific layout
            if (NotFound == null)
            {
                throw new InvalidOperationException($"The {nameof(Router)} component requires a value for the parameter {nameof(NotFound)}.");
            }

            if (!_onNavigateCalled)
            {
                _onNavigateCalled = true;
                await RunOnNavigateWithRefreshAsync(NavigationManager!.ToBaseRelativePath(_locationAbsolute), isNavigationIntercepted: false);
                return;
            }

            Refresh(isNavigationIntercepted: false);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            NavigationManager!.LocationChanged -= OnLocationChanged;
        }

        private static string StringUntilAny(string str, char[] chars)
        {
            var firstIndex = str.IndexOfAny(chars);
            return firstIndex < 0
                ? str
                : str.Substring(0, firstIndex);
        }

        private void RefreshRouteTable()
        {
            var assemblies = AdditionalAssemblies == null ? new[] { AppAssembly } : new[] { AppAssembly }.Concat(AdditionalAssemblies);
            var assembliesSet = new HashSet<Assembly>(assemblies!);

            if (!_assemblies.SetEquals(assembliesSet))
            {
                RouteContainer?.Merge(RouteTableFactory.GetRouteEntries(assemblies!));
                _assemblies.Clear();
                _assemblies.UnionWith(assembliesSet);
            }

        }

        internal virtual void Refresh(bool isNavigationIntercepted)
        {
            // If an `OnNavigateAsync` task is currently in progress, then wait
            // for it to complete before rendering. Note: because _previousOnNavigateTask
            // is initialized to a CompletedTask on initialization, this will still
            // allow first-render to complete successfully.
            if (_previousOnNavigateTask.Status != TaskStatus.RanToCompletion)
            {
                if (Navigating != null)
                {
                    _renderHandle?.Render(Navigating);

                }
                return;
            }

            RefreshRouteTable();

            var locationPath = NavigationManager!.ToBaseRelativePath(_locationAbsolute);
            locationPath = StringUntilAny(locationPath, _queryOrHashStartChar);

            var match = RouteContainer?.TryMatch(locationPath);

            if (match?.IsSuccess ?? false && _lastRender != match.Value.RouteEntry.Handler)
            {
                _lastRender = match!.Value.RouteEntry!.Handler;
                if (!typeof(IComponent).IsAssignableFrom(match.Value.RouteEntry?.Handler))
                {
                    throw new InvalidOperationException($"The type {match.Value.RouteEntry?.Handler!.FullName} " +
                        $"does not implement {typeof(IComponent).FullName}.");
                }

                Log.NavigatingToComponent(_logger!, match.Value.RouteEntry?.Handler!, locationPath, _baseUri!);

                var routeData = new RouteData(
                    match.Value.RouteEntry!.Handler,
                    match.Value.Parameters ?? _emptyParametersDictionary!);
                _renderHandle?.Render(Found!(routeData));
            }
            else
            {
                if (!isNavigationIntercepted)
                {
                    Log.DisplayingNotFound(_logger!, locationPath, _baseUri!);

                    // We did not find a Component that matches the route.
                    // Only show the NotFound content if the application developer programatically got us here i.e we did not
                    // intercept the navigation. In all other cases, force a browser navigation since this could be non-Blazor content.
                    _renderHandle?.Render(NotFound);
                }
                else
                {
                    Log.NavigatingToExternalUri(_logger!, _locationAbsolute!, locationPath, _baseUri!);
                    NavigationManager.NavigateTo(_locationAbsolute, forceLoad: true);
                }
            }
        }

        private async ValueTask<bool> RunOnNavigateAsync(string path, Task previousOnNavigate)
        {

            // Cancel the CTS instead of disposing it, since disposing does not
            // actually cancel and can cause unintended Object Disposed Exceptions.
            // This effectivelly cancels the previously running task and completes it.
            _onNavigateCts?.Cancel();
            // Then make sure that the task has been completely cancelled or completed
            // before starting the next one. This avoid race conditions where the cancellation
            // for the previous task was set but not fully completed by the time we get to this
            // invocation.
            await previousOnNavigate;

            if (OnNavigateAsync == null)
            {
                return true;
            }

            _onNavigateCts = new CancellationTokenSource();
            var navigateContext = new NavigationContext(path, _onNavigateCts.Token);

            try
            {
                if (Navigating != null)
                {
                    _renderHandle?.Render(Navigating);
                }
                await OnNavigateAsync(navigateContext);
                return true;
            }
            catch (OperationCanceledException e)
            {
                if (e.CancellationToken != navigateContext.CancellationToken)
                {
                    var rethrownException = new InvalidOperationException("OnNavigateAsync can only be cancelled via NavigateContext.CancellationToken.", e);
                    _renderHandle?.Render(builder => ExceptionDispatchInfo.Throw(rethrownException));
                }
            }
            catch (Exception e)
            {
                _renderHandle?.Render(builder => ExceptionDispatchInfo.Throw(e));
            }

            return false;
        }

        internal async Task RunOnNavigateWithRefreshAsync(string path, bool isNavigationIntercepted)
        {
            // We cache the Task representing the previously invoked RunOnNavigateWithRefreshAsync
            // that is stored. Then we create a new one that represents our current invocation and store it
            // globally for the next invocation. This allows us to check inside `RunOnNavigateAsync` if the
            // previous OnNavigateAsync task has fully completed before starting the next one.
            var previousTask = _previousOnNavigateTask;
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _previousOnNavigateTask = tcs.Task;

            // And pass an indicator for the previous task to the currently running one.
            var shouldRefresh = await RunOnNavigateAsync(path, previousTask);
            tcs.SetResult(true);
            if (shouldRefresh)
            {
                Refresh(isNavigationIntercepted);
            }

        }

        private void OnLocationChanged(object sender, LocationChangedEventArgs args)
        {
            _locationAbsolute = args.Location;
            if (_renderHandle?.IsInitialized ?? false && RouteContainer != null)
            {
                _ = RunOnNavigateWithRefreshAsync(NavigationManager!.ToBaseRelativePath(_locationAbsolute), args.IsNavigationIntercepted);
            }
        }

        Task IHandleAfterRender.OnAfterRenderAsync()
        {
            if (!_navigationInterceptionEnabled)
            {
                _navigationInterceptionEnabled = true;
                return NavigationInterception!.EnableNavigationInterceptionAsync();
            }

            return Task.CompletedTask;
        }

        private static class Log
        {
            private static readonly Action<ILogger, string, string, Exception?> _displayingNotFound =
                LoggerMessage.Define<string, string>(LogLevel.Debug, new EventId(1, "DisplayingNotFound"), $"Displaying {nameof(NotFound)} because path '{{Path}}' with base URI '{{BaseUri}}' does not match any component route");

            private static readonly Action<ILogger, Type, string, string, Exception?> _navigatingToComponent =
                LoggerMessage.Define<Type, string, string>(LogLevel.Debug, new EventId(2, "NavigatingToComponent"), "Navigating to component {ComponentType} in response to path '{Path}' with base URI '{BaseUri}'");

            private static readonly Action<ILogger, string, string, string, Exception?> _navigatingToExternalUri =
                LoggerMessage.Define<string, string, string>(LogLevel.Debug, new EventId(3, "NavigatingToExternalUri"), "Navigating to non-component URI '{ExternalUri}' in response to path '{Path}' with base URI '{BaseUri}'");

            internal static void DisplayingNotFound(ILogger logger, string path, string baseUri)
            {
                _displayingNotFound(logger, path, baseUri, null);
            }

            internal static void NavigatingToComponent(ILogger logger, Type componentType, string path, string baseUri)
            {
                _navigatingToComponent(logger, componentType, path, baseUri, null);
            }

            internal static void NavigatingToExternalUri(ILogger logger, string externalUri, string path, string baseUri)
            {
                _navigatingToExternalUri(logger, externalUri, path, baseUri, null);
            }
        }
    }
}
