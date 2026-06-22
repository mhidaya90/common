using Acr.UserDialogs;
using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Locations;
using Android.OS;
using Android.Runtime;
using AndroidX.AppCompat.App;
using AndroidX.Core.App;
using CommunityToolkit.Mvvm.Messaging;
using GPD.Core.Constants;
using GPD.Core.Extensions;
using GPD.Core.Helpers;
using GPD.Core.Interfaces.Services;
using GPD.Core.Messages;
using GPD.Core.ViewModels.Login;
using GPD.Presentation.Platforms.Android.Listeners;
using GPD.Presentation.Platforms.Android.Receivers;
using GPD.Presentation.Platforms.Android.Services;
using GPD.Presentation.Services;
using Java.Lang;
using Java.Util;
using Microsoft.Identity.Client;
using Location = Android.Locations.Location;
using Exception = System.Exception;
using Handler = Android.OS.Handler;

namespace GPD.Presentation
{
    [Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, ScreenOrientation = ScreenOrientation.Portrait, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    public class MainActivity : MauiAppCompatActivity, ILocationListener, IServiceConnection
    {
        private LocationManager _locationManager;
        private ILocalization _localization;
        private IPermissionsStatusService _permissionsStatusService;
        private IBarcodeScannerService _barcodeScannerService;
        private ISerilogService _logger;
        private IAutoLogoutService _autoLogoutService;

        private string[] _permissionNames = new string[] { Manifest.Permission.Camera, Manifest.Permission.AccessFineLocation, Manifest.Permission.AccessCoarseLocation };

        private Handler handler;
        private Runnable runnable;

        private bool _alarmPermissionRequested;
        public static MainActivity Instance { get; private set; }
        public Location CurrentLocation { get; set; }
        public static bool SaveInstanceStateCalled { get; set; }
        public TundraCopilotListener CopilotListener { get; set; }

        protected override void OnCreate(Bundle savedInstanceState)
        {
            AppCompatDelegate.DefaultNightMode = AppCompatDelegate.ModeNightNo;
            base.OnCreate(savedInstanceState);

            try
            {
                AppCompatDelegate.DefaultNightMode = AppCompatDelegate.ModeNightNo;
                BarcodeScannerService.Initialize(this, Platform.CurrentActivity.ApplicationContext);
                AndroidCopilotService.Initialize(this);
                UserDialogs.Init(this);
                ExceptionExtension.InitializationExtension(ServiceHelper.GetService<IFirebaseAnalyticsService>());
                _localization = ServiceHelper.GetService<ILocalization>();
                _permissionsStatusService = ServiceHelper.GetService<IPermissionsStatusService>();
                _barcodeScannerService = ServiceHelper.GetService<IBarcodeScannerService>();
                _logger = ServiceHelper.GetService<ISerilogService>();
                AndroidEnvironment.UnhandledExceptionRaiser += (sender, e) =>
                {
                    _logger.WriteToErrorLog($"Exception in MainAxtivity : {e.Exception}");
                };

                Instance = this;


                _autoLogoutService = ServiceHelper.GetService<IAutoLogoutService>();

                // Initializing the handler and the runnable
                handler = new Handler(Looper.MainLooper);
                runnable = new Runnable(() =>
                {
                    try
                    {
                        _logger.WriteToInfoLog($"MainActivity.OnCreate(): Calling Logout from Background/Inactivity Process");
                        _ = Task.Run(async () => await _autoLogoutService.Logout().ConfigureAwait(false));
                        StopHandler();
                    }
                    catch (Exception ex)
                    {
                        ExceptionExtension.TrackError(ex);
                    }
                });
            }catch (Exception ex)
            {
                ExceptionExtension.TrackError(ex);
            }
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent data)
        {
            base.OnActivityResult(requestCode, resultCode, data);
            try
            {
                if (data == null)
                {
                    _logger?.WriteToErrorLog("OnActivityResult: Intent data is null. Possible cause for MSAL crash.");
                }
                AuthenticationContinuationHelper.SetAuthenticationContinuationEventArgs(requestCode, resultCode, data);
            }catch (Exception ex)
            {
                _logger?.WriteToErrorLog($"OnActivityResult Exception: {ex}");
                ExceptionExtension.TrackError(ex);
            }
        }

        public async void RequestPermissions()
        {
            try
            {
                // Get the initial state of the permissions
                RefreshPermissionsStatus();
                // Check if we have any permissions that are not granted. If so, show a message to the user telling them
                // the permissions are required and then request the permissions.

                if (!_permissionsStatusService.AreAllPermissionsGranted)
                {
                    // Only request permissions that have not been permanently denied
                    var permissionsToRequest = _permissionNames.Where(p =>
                        ActivityCompat.CheckSelfPermission(this, p) != Android.Content.PM.Permission.Granted &&
                        ActivityCompat.ShouldShowRequestPermissionRationale(this, p)).ToArray();
                    // If no permissions can be requested (all permanently denied), skip the request
                    if (!permissionsToRequest.Any())
                    {
                        // Check if any permissions are still not granted but permanently denied
                        var deniedPermissions = _permissionNames.Where(p =>
                            ActivityCompat.CheckSelfPermission(this, p) != Android.Content.PM.Permission.Granted).ToArray();
                        if (deniedPermissions.Any())
                        {
                            // First time requesting - ShouldShowRequestPermissionRationale returns false before first request
                            ActivityCompat.RequestPermissions(this, deniedPermissions, 1);
                        }
                        return;
                    }
                    var result = await LoginViewModel.ShowPermissionsErrorPopUp().ConfigureAwait(false);

                    if (result == true)
                    {
                        ActivityCompat.RequestPermissions(this, permissionsToRequest, 1);
                        return;
                    }
                }
            }catch (Exception ex)
            {
                ExceptionExtension.TrackError(ex);
            }
        }

        protected override void OnResume()
        {
            try
            {
                base.OnResume();
                _barcodeScannerService.EnableScanner();

                // The app has resumed, update any listeners of the permission service.
                _permissionsStatusService.UpdateListeners();
                // If the user was redirected to grant exact alarm permission, try scheduling now
                if (_alarmPermissionRequested)
                {
                    TryScheduleAlarm();
                }
            }catch (System.Exception ex)
            {
                _logger.WriteToErrorLog($"MainActivity.OnResume Exception: {ex}");

                ExceptionExtension.TrackError(ex);
            }
        }
        private void TryScheduleAlarm()
        {
            try
            {
                var alarmManager = (AlarmManager)GetSystemService(AlarmService);

                if (Build.VERSION.SdkInt >= BuildVersionCodes.S)
                {
                    if (!alarmManager.CanScheduleExactAlarms())
                    {
                        if (!_alarmPermissionRequested)
                        {
                            _alarmPermissionRequested = true;
                            // Redirect user to grant exact alarm permission (one-time prompt)
                            Intent intent = new Intent(Android.Provider.Settings.ActionRequestScheduleExactAlarm);
                            intent.SetData(Android.Net.Uri.Parse("package:" + PackageName));
                            StartActivity(intent);
                        }
                        else
                        {
                            _logger?.WriteToInfoLog("Exact alarm permission not granted. User was already prompted.");
                        }
                        return;
                    }
                }
                AlarmHelper.ScheduleDailyAlarm(Android.App.Application.Context);
            }
            catch (Exception ex)
            {
                ExceptionExtension.TrackError(ex);
            }
        }
        protected override void OnStop()
        {
            try
            {
                base.OnStop();

                _barcodeScannerService.DisableScanner();
            }
            catch (Exception ex)
            {
                ExceptionExtension.TrackError(ex);
            }
        }

        protected override void OnPause()
        {
            try
            {
                base.OnPause();
                _logger.WriteToInfoLog("MainActivity.OnPause called");
                StartHandler();
            }
            catch (Exception ex)
            {
                ExceptionExtension.TrackError(ex);
                _logger.WriteToErrorLog($"MainActivity.OnPause Exception: {ex}");
            }
        }

        public override void OnUserInteraction()
        {
            try
            {
                base.OnUserInteraction();
                StopHandler();
            }
            catch (Exception ex)
            {
                ExceptionExtension.TrackError(ex);
                _logger.WriteToErrorLog($"MainActivity.OnUserInteraction Exception: {ex}");
            }
        }

        /// <summary>
        /// Method to stop handler when application comes back from inactive/background state.
        /// </summary>
        public void StopHandler()
        {
            try
            {
                handler.RemoveCallbacks(runnable);

            }
            catch (Exception ex)
            {
                ExceptionExtension.TrackError(ex);
                _logger.WriteToErrorLog($"MainActivity.StopHandler Exception: {ex}");
            }
        }

        /// <summary>
        /// Method to start handler when application goes inactive or in the background.
        /// </summary>
        public void StartHandler()
        {
            try
            {
                handler.PostDelayed(runnable, AppConstants.InactiveIntervalTimeInMilliseconds);
            }
            catch (Exception ex)
            {
                ExceptionExtension.TrackError(ex);
                _logger.WriteToErrorLog($"MainActivity.StartHandler Exception: {ex}");
            }
        }

        /// <summary>
        /// Ensure Android permissions are available and request if not.
        /// </summary>
        /// <param name="requestCode">Request Code.</param>
        /// <param name="permissions">Permissions.</param>
        /// <param name="grantResults">Grant results.</param>
        public override async void OnRequestPermissionsResult(int requestCode, string[] permissions, [GeneratedEnum] Android.Content.PM.Permission[] grantResults)
        {
            try
            {
                // Check if we have permissions results. We may not have them yet, in which case grantResults will be empty. If
                // we have results, send them along to the permissions service so it can notify any listeners. The Permission
                // enum array is converted to an integer array so we don't have to take a reference to Android in the Core project. That
                // would be bad.
                if (grantResults.Any())
                {
                    _permissionsStatusService.SetPermissionStatus(Array.ConvertAll(grantResults, value => (int)value));
                }

                if (grantResults.Any() && !grantResults.Contains(Android.Content.PM.Permission.Denied))
                {
                    WeakReferenceMessenger.Default.Send(new PassMessage(this, "PermissionGranted"));
                    // Now that camera/location permissions are granted, request alarm permission
                    TryScheduleAlarm();
                }

                // Check if we have any permissions that were not granted.
                else if (grantResults.Any() && grantResults.Contains(Android.Content.PM.Permission.Denied))
                {
                    // Only re-request permissions that the user can still grant (not permanently denied).
                    var deniedPermissions = permissions
                        .Where((p, i) => grantResults[i] == Android.Content.PM.Permission.Denied)
                        .ToArray();
                    var canRequestAgain = deniedPermissions
                        .Where(p => ActivityCompat.ShouldShowRequestPermissionRationale(this, p))
                        .ToArray();
                    if (canRequestAgain.Any())
                    {
                        var result = await LoginViewModel.ShowPermissionsErrorPopUp().ConfigureAwait(false);
                        if (result == true)
                        {
                            ActivityCompat.RequestPermissions(this, canRequestAgain, 1);
                            return;
                        }
                    }

                }

                if (permissions.Contains(Manifest.Permission.AccessFineLocation))
                {
                    _locationManager = (LocationManager)GetSystemService(LocationService);

                }
            }
            catch (Exception ex)
            {
                ExceptionExtension.TrackError(ex);
            }
        }

        /// <summary>
        /// The app has returned from the background. During the time it was in the background, who knows what the user was doing??
        /// What we are concerned with them doing was granting previously ungranted permissions. If they revoked permissions, Android will
        /// kill the app, but Android will do nothing if permissions were granted. So, check the state of the permissions here and
        /// tell the service so it can tell any listeners.
        /// </summary>
        protected override void OnRestart()
        {
            try
            {
                base.OnRestart();

                if (!_permissionsStatusService.AreAllPermissionsGranted)
                {
                    RefreshPermissionsStatus();
                }
            }
            catch (Exception ex)
            {
                ExceptionExtension.TrackError(ex);
            }
        }

        /// <summary>
        /// Gets the current state of the required permissions and updates the PermissionsStatusService with the results.
        /// </summary>
        private void RefreshPermissionsStatus()
        {
            try
            {
                var grantResults = new List<int>();
                Array.ForEach(_permissionNames, (value) => grantResults.Add(ActivityCompat.CheckSelfPermission(ApplicationContext, value) == Android.Content.PM.Permission.Denied ? -1 : 0));
                _permissionsStatusService.SetPermissionStatus(grantResults.ToArray());
            }
            catch (Exception ex)
            {
                ExceptionExtension.TrackError(ex);
            }
        }
        public void OnLocationChanged(Android.Locations.Location location)
        {
            try
            {
                if (location == null)
                {
                    return;
                }

                CurrentLocation = location;
            }
            catch (Exception ex)
            {
                ExceptionExtension.TrackError(ex);
            }
        }

        public void OnProviderDisabled(string provider)
        {
        }
        public void OnProviderEnabled(string provider)
        {
        }
        public void OnStatusChanged(string provider, [GeneratedEnum] Availability status, Bundle extras)
        {
        }
        public void OnServiceConnected(ComponentName name, IBinder service)
        {
        }
        public void OnServiceDisconnected(ComponentName name)
        {
        }
        protected override void OnPostCreate(Bundle savedInstanceState)
        {
            try
            {
                base.OnPostCreate(savedInstanceState);
            }
            catch (Exception ex)
            {
                ExceptionExtension.TrackError(ex);
            }
        }
    }
}