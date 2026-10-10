using UnityEngine;

namespace ImageDownloaderDemo
{
    /// <summary>
    /// Minimal self-contained MonoBehaviour singleton.
    /// The original component in the game project depended on a project-wide singleton base
    /// that pulled in an event bus; this tiny version keeps the demo dependency-free.
    /// </summary>
    public abstract class MonoSingleton<T> : MonoBehaviour where T : MonoBehaviour
    {
        private static T _instance;
        private static bool _quitting;

        public static T Instance
        {
            get
            {
                if (_instance == null && !_quitting)
                {
                    var go = new GameObject(typeof(T).Name);
                    _instance = go.AddComponent<T>();
                }

                return _instance;
            }
        }

        public static bool HasInstance => _instance != null;

        /// <summary>When true the singleton survives scene loads (DontDestroyOnLoad).</summary>
        protected virtual bool PersistAcrossScenes => true;

        protected virtual void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Debug.LogWarning($"[{typeof(T).Name}] Duplicate instance detected, destroying the new one.");
                Destroy(gameObject);
                return;
            }

            _instance = this as T;
            if (PersistAcrossScenes) DontDestroyOnLoad(gameObject);
            OnInit();
        }

        protected virtual void OnDestroy()
        {
            OnCleanup();
            if (_instance == this) _instance = null;
        }

        protected virtual void OnApplicationQuit()
        {
            // Do not lazily create instances while the application is shutting down.
            _quitting = true;
        }

        /// <summary>Called once after the singleton instance is created.</summary>
        protected virtual void OnInit() { }

        /// <summary>Called when the singleton is destroyed.</summary>
        protected virtual void OnCleanup() { }
    }
}
