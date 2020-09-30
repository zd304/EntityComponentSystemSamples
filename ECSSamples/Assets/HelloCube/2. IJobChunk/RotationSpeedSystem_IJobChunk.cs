using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;

/// <summary>
/// 这里利用了Jobs和Burst编译器，它们和ECS共同组成DOTS，使代码运行更加高效
/// </summary>
public class RotationSpeedSystem_IJobChunk : SystemBase
{
    EntityQuery m_Group;

    protected override void OnCreate()
    {
        // Cached access to a set of ComponentData based on a specific query
        m_Group = GetEntityQuery(typeof(Rotation), ComponentType.ReadOnly<RotationSpeed_IJobChunk>());
    }

    // //使用这个Attribute来利用Burst编译器，需要在Unity编辑器菜单栏Jobs > Burst > Enable Compilition来激活编译器
    [BurstCompile]
    struct RotationSpeedJob : IJobChunk
    {
        public float DeltaTime;
        public ArchetypeChunkComponentType<Rotation> RotationType;
        [ReadOnly] public ArchetypeChunkComponentType<RotationSpeed_IJobChunk> RotationSpeedType;

        public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
        {
            var chunkRotations = chunk.GetNativeArray(RotationType);
            var chunkRotationSpeeds = chunk.GetNativeArray(RotationSpeedType);
            for (var i = 0; i < chunk.Count; i++)
            {
                var rotation = chunkRotations[i];
                var rotationSpeed = chunkRotationSpeeds[i];

                // Rotate something about its up vector at the speed given by RotationSpeed_IJobChunk.
                chunkRotations[i] = new Rotation
                {
                    Value = math.mul(math.normalize(rotation.Value),
                        quaternion.AxisAngle(math.up(), rotationSpeed.RadiansPerSecond * DeltaTime))
                };
            }
        }
    }

    // OnUpdate跑在主线程上，并将Job任务加入到计划中
    protected override void OnUpdate()
    {
        // IJobChunk需要根据Archetype来找到Entity对应的IComponentData，来进行计算
        // Explicitly declare:
        // - Read-Write access to Rotation
        // - Read-Only access to RotationSpeed_IJobChunk
        var rotationType = GetArchetypeChunkComponentType<Rotation>();
        var rotationSpeedType = GetArchetypeChunkComponentType<RotationSpeed_IJobChunk>(true);

        // 创建IJobChunk任务并加入到计划中
        var job = new RotationSpeedJob()
        {
            RotationType = rotationType,
            RotationSpeedType = rotationSpeedType,
            DeltaTime = Time.DeltaTime
        };

        Dependency = job.Schedule(m_Group, Dependency);
    }
}
