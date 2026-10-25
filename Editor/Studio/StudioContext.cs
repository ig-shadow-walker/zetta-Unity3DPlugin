using System;
using System.Threading.Tasks;

namespace Zetta3D.Editor.Studio
{
    /// <summary>
    /// What every Studio panel needs to know about the account: plan gates (3D and 2D), the price
    /// list and the image models. Loaded once per sign-in and kept across window reopens.
    /// A null gate means "unknown" — panels never lock anything on an unknown gate.
    /// </summary>
    public class StudioContext
    {
        public ThreeDEntitlements Gates3D;
        public ImageEntitlements Gates2D;
        public PriceList Prices;
        public ImageModels ImageModels;
        public bool Loading;
        public string LoadError;

        /// <summary>Raised on the main thread after the account data (re)loads.</summary>
        public event Action Changed;

        /// <summary>The window hosting the panels; used for tab switching and hand-offs.</summary>
        public ZettaStudioWindow Window;

        static StudioContext _shared;
        static string _loadedFor;
        static string _dataFor;

        /// <summary>The one context for the signed-in account.</summary>
        public static StudioContext Shared => _shared ??= new StudioContext();

        public long Credits => ZettaAuth.User?.Credits?.ThreeD?.Total ?? 0;

        static string AccountKey => ZettaAuth.User == null ? null : $"{ZettaConfig.ApiBase}|{ZettaAuth.User.Id}";

        /// <summary>Loads the account data unless it is already loaded for this account.</summary>
        public void EnsureLoaded()
        {
            var key = AccountKey;
            if (key == null || Loading || _loadedFor == key) return;
            if (_dataFor != key)
            {
                // A different account: never show the last one's plan or prices while loading.
                Gates3D = null;
                Gates2D = null;
                Prices = null;
                ImageModels = null;
            }
            _ = LoadAsync();
        }

        public async Task LoadAsync()
        {
            var key = AccountKey;
            if (key == null) return;
            Loading = true;
            LoadError = null;
            try
            {
                var gates3D = Try(ZettaApi.Entitlements3DAsync);
                var gates2D = Try(ZettaApi.Entitlements2DAsync);
                var prices = Try(ZettaApi.PricingAsync);
                var models = Try(ZettaApi.ImageModelsAsync);
                await Task.WhenAll(gates3D, gates2D, prices, models);
                Gates3D = gates3D.Result.value;
                Gates2D = gates2D.Result.value;
                Prices = prices.Result.value;
                ImageModels = models.Result.value;
                LoadError = prices.Result.error ?? gates3D.Result.error ?? models.Result.error ?? gates2D.Result.error;
                _loadedFor = LoadError == null ? key : null;
                _dataFor = key;
            }
            finally
            {
                Loading = false;
                Changed?.Invoke();
            }
        }

        static async Task<(T value, string error)> Try<T>(Func<Task<T>> call) where T : class
        {
            try
            {
                return (await call(), null);
            }
            catch (ZettaApiException e)
            {
                return (null, ZettaCatalog.Explain(e));
            }
            catch (Exception e)
            {
                return (null, e.Message);
            }
        }

        /// <summary>Forgets the account data (sign-out, or a different account signed in).</summary>
        public void Reset()
        {
            Gates3D = null;
            Gates2D = null;
            Prices = null;
            ImageModels = null;
            _loadedFor = null;
            _dataFor = null;
            Changed?.Invoke();
        }

        /// <summary>After a job is charged: re-read the balance for the header and cost lines.</summary>
        public static void RefreshCredits() => _ = ZettaAuth.RefreshAsync();
    }
}
