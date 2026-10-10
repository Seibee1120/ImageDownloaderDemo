using UnityEngine;

namespace ImageDownloaderDemo
{
    /// <summary>
    ///     MonoBehaviour 单例
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

        // 为 true 时，跨场景保留
        protected virtual bool PersistAcrossScenes => true;

        protected virtual void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Debug.LogWarning($"[{typeof(T).Name}] 检测到重复实例，销毁新创建的实例。");
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
            _quitting = true;
        }

        // 在单例实例创建完成后调用一次。
        protected virtual void OnInit()
        {
        }

        // 在单例被销毁时调用。
        protected virtual void OnCleanup()
        {
        }
    }
}