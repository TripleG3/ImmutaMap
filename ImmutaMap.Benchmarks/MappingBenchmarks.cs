using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
// MemoryDiagnoser attribute comes from BenchmarkDotNet.Attributes
using ImmutaMap;
using ImmutaMap.Test; // reuse test models
using ImmutaMap.Builders;

namespace ImmutaMap.Benchmarks;

[MemoryDiagnoser]
public class MappingBenchmarks
{
    private PersonClass _personClass = new("John","Doe",42);
    private MessageDto _messageDto = new(){ Msg = "Hello", TimeStamp = DateTime.UtcNow, Modified = DateTime.UtcNow };
    private Configuration<PersonClass, PersonClass> _selfConfig = Configuration<PersonClass, PersonClass>.Empty;
    private Configuration<MessageDto, Message> _dtoToMessage = Configuration<MessageDto, Message>.Empty;
    private TargetBuilder _builder = TargetBuilder.GetNewInstance();

    [Benchmark]
    public PersonClass PersonSelfMap() => _builder.Build(_selfConfig, _personClass)!;

    [Benchmark]
    public Message DtoToMessageMap() => _builder.Build(_dtoToMessage, _messageDto)!;

    [Benchmark]
    public PersonClass ManualReflectionCopy()
    {
        var source = _personClass;
        var result = (PersonClass)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(PersonClass));
        var ctor = typeof(PersonClass).GetConstructors().First();
        // simulate naive manual mapping (reconstruction)
        return (PersonClass)ctor.Invoke(new object[]{source.FirstName, source.LastName, source.Age});
    }

    [GlobalSetup]
    public void Setup()
    {
        // Warm caches
    _builder.Build(_selfConfig, _personClass);
    _builder.Build(_dtoToMessage, _messageDto);
    }
}

public static class Program
{
    public static void Main(string[] args)
    {
        BenchmarkRunner.Run<MappingBenchmarks>();
    }
}
