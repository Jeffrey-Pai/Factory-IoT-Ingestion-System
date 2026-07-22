namespace FactoryIoT.Simulator.Models;

/// <summary>Represents a factory machine being simulated.</summary>
public sealed record MachineDescriptor(
    string MachineId,
    string[] SensorTypes);
