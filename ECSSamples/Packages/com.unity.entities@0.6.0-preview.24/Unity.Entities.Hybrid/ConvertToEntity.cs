using System;
using System.Collections.Generic;
using Unity.Entities.Conversion;
using UnityEngine;
using UnityObject = UnityEngine.Object;
using static Unity.Debug;

namespace Unity.Entities
{
    /// <summary>
    /// 将游戏对象转化成ECS的实体Entity
    /// </summary>
    [DisallowMultipleComponent]//不允许多组件
    [AddComponentMenu("DOTS/Convert To Entity")]
    public class ConvertToEntity : MonoBehaviour
    {
        /// <summary>
        /// 转化模式枚举
        /// </summary>
        public enum Mode
        {
            ConvertAndDestroy,//转化并摧毁，该模式下GameObject在被转化成实体Entity后原游戏对象被摧毁
            ConvertAndInjectGameObject//转化并注入游戏对象，这个模式不会摧毁原来的游戏对象
        }

        /// <summary>
        /// 转化模式
        /// </summary>
        public Mode ConversionMode;

        void Awake()
        {
            if (World.DefaultGameObjectInjectionWorld != null)
            {
                var system = World.DefaultGameObjectInjectionWorld.GetOrCreateSystem<ConvertToEntitySystem>();
                // step 1: 将当前GameObject加入到ConvertToEntitySystem的m_ToBeConverted里，等待转换为Entity
                system.AddToBeConverted(World.DefaultGameObjectInjectionWorld, this);
            }
            else
            {
                UnityEngine.Debug.LogWarning($"{nameof(ConvertToEntity)} failed because there is no {nameof(World.DefaultGameObjectInjectionWorld)}", this);
            }
        }
    }

    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public class ConvertToEntitySystem : ComponentSystem
    {
        Dictionary<World, List<ConvertToEntity>> m_ToBeConverted = new Dictionary<World, List<ConvertToEntity>>();

        public BlobAssetStore BlobAssetStore { get; private set; }

        protected override void OnCreate()
        {
            base.OnCreate();
            BlobAssetStore = new BlobAssetStore();
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            if (BlobAssetStore != null)
            {
                BlobAssetStore.Dispose();
                BlobAssetStore = null;
            }
        }

        // using `this.World` is a sign of a problem - that World is only needed so that this system will update, but
        // adding entities to it directly is wrong (must be directed via m_ToBeConverted).
        // ReSharper disable once UnusedMember.Local
        new World World => throw new InvalidOperationException($"Do not use `this.World` directly (use {nameof(m_ToBeConverted)})");

        protected override void OnUpdate()
        {
            if (m_ToBeConverted.Count != 0)
                Convert();
        }

        public void AddToBeConverted(World world, ConvertToEntity convertToEntity)
        {
            // step 2: 根据不同的world，将ConvertToEntity分配到不同组里
            if (!m_ToBeConverted.TryGetValue(world, out var list))
            {
                list = new List<ConvertToEntity>();
                m_ToBeConverted.Add(world, list);
            }
            list.Add(convertToEntity);
        }

        static bool IsConvertAndInject(GameObject go)
        {
            var mode = go.GetComponent<ConvertToEntity>()?.ConversionMode;
            return mode == ConvertToEntity.Mode.ConvertAndInjectGameObject;
        }

        static void AddRecurse(EntityManager manager, Transform transform, HashSet<Transform> toBeDetached, List<Transform> toBeInjected)
        {
            if (transform.GetComponent<StopConvertToEntity>() != null)
            {
                toBeDetached.Add(transform);
                return;
            }

            GameObjectEntity.AddToEntityManager(manager, transform.gameObject);

            if (IsConvertAndInject(transform.gameObject))
            {
                toBeDetached.Add(transform);
                toBeInjected.Add(transform);
            }
            else
            {
                foreach (Transform child in transform)
                    AddRecurse(manager, child, toBeDetached, toBeInjected);
            }
        }

        static void InjectOriginalComponents(GameObjectConversionMappingSystem mappingSystem, Transform transform)
        {
            // step 6-1: 根据GameObject，分配一个Entity（其实就是个索引）
            var entity = mappingSystem.GetPrimaryEntity(transform.gameObject);
            foreach (var com in transform.GetComponents<Component>())
            {
                if (com is GameObjectEntity || com is ConvertToEntity || com is ComponentDataProxyBase || com is StopConvertToEntity)
                    continue;
                // step 6-2: 将所有组件附加到Entity上
                // 注意：这里之所以可以将UnityEngine.Component当做ComponentData，请参看[GenerateAuthoringComponent]这个Attribute的功能
                mappingSystem.DstEntityManager.AddComponentObject(entity, com);
            }
        }

        void Convert()
        {
            var toBeDetached = new HashSet<Transform>();
            var conversionRoots = new HashSet<GameObject>();

            try
            {
                var toBeInjected = new List<Transform>();

                // step 3: 遍历所有m_ToBeConverted，即等待转换为Entity的GameObject
                foreach (var convertToWorld in m_ToBeConverted)
                {
                    var toBeConverted = convertToWorld.Value;

                    var settings = new GameObjectConversionSettings(
                        convertToWorld.Key,
                        GameObjectConversionUtility.ConversionFlags.AssignName);
                    
                    settings.BlobAssetStore = BlobAssetStore;

                    using (var gameObjectWorld = settings.CreateConversionWorld())
                    {
                        // step 4: 删除所有父节点带有ConvertToEntity脚本和StopConvertToEntity脚本的节点
                        toBeConverted.RemoveAll(convert =>
                        {
                            if (convert.GetComponent<StopConvertToEntity>() != null)
                            {
                                LogWarning(
                                    $"{nameof(ConvertToEntity)} will be ignored because of a {nameof(StopConvertToEntity)} on the same GameObject",
                                    convert.gameObject);
                                return true;
                            }

                            var parent = convert.transform.parent;
                            var remove = parent != null && parent.GetComponentInParent<ConvertToEntity>() != null;
                            if (remove && parent.GetComponentInParent<StopConvertToEntity>() != null)
                            {
                                LogWarning(
                                    $"{nameof(ConvertToEntity)} will be ignored because of a {nameof(StopConvertToEntity)} higher in the hierarchy",
                                    convert.gameObject);
                            }

                            return remove;
                        });

                        // step 5: 遍历访问所有子节点，并将它们纳入m_ToBeConverted
                        foreach (var convert in toBeConverted)
                            AddRecurse(gameObjectWorld.EntityManager, convert.transform, toBeDetached, toBeInjected);

                        foreach (var convert in toBeConverted)
                        {
                            conversionRoots.Add(convert.gameObject);
                            toBeDetached.Remove(convert.transform);
                        }

                        GameObjectConversionUtility.Convert(gameObjectWorld);

                        var mappingSystem = gameObjectWorld.GetExistingSystem<GameObjectConversionMappingSystem>();
                        foreach (var convert in toBeInjected)
                        {
                            // step 6: 将GameObject转换成Entity
                            InjectOriginalComponents(mappingSystem, convert);
                        }
                    }

                    toBeInjected.Clear();
                }
            }
            finally
            {
                m_ToBeConverted.Clear();

                foreach (var transform in toBeDetached)
                    transform.parent = null;

                foreach (var go in conversionRoots)
                {
                    if(!IsConvertAndInject(go))
                        UnityObject.DestroyImmediate(go);
                }
            }
        }
    }
}
