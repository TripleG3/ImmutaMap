using BenchmarkDotNet.Attributes;
using ImmutaMap.Test;

namespace ImmutaMap.Benchmarks;

[MemoryDiagnoser]
[RankColumn]
public class MappingBenchmarks
{
    private PersonRecord _personRecord = null!;
    private PersonClass _personClass = null!;
    private List<MessageDto> _messages = null!;
    private Master _master = null!;
    private Slave _slave = null!;
    private SlaveOdd _slaveOdd = null!;

    [GlobalSetup]
    public void Setup()
    {
        _personRecord = new PersonRecord("FirstMock1", "LastMock1", 50);
        _personClass = new PersonClass("FirstMock1", "LastMock1", 50);
        _messages = new List<MessageDto>
        {
            new MessageDto{ Msg = "Mock1", TimeStamp = DateTime.UtcNow.AddHours(-4), Modified = DateTime.UtcNow },
            new MessageDto{ Msg = "Mock2", TimeStamp = DateTime.UtcNow.AddHours(-3), Modified = DateTime.UtcNow },
            new MessageDto{ Msg = "Mock3", TimeStamp = DateTime.UtcNow.AddHours(-2), Modified = DateTime.UtcNow },
        };
        _master = new Master
        {
            Count = 50,
            Item1 = "Mock1",
            Item2 = "Mock2",
            Item3 = "Mock3"
        };
        _slave = new Slave { Count = 100, Item2 = "Slave2" };
        _slaveOdd = new SlaveOdd { Counter = 100, Item_2 = "Slave2" };
    }

    // 1. PersonRecord -> PersonClass
    [Benchmark]
    public PersonClass Map_PersonRecord_To_PersonClass_Library() => _personRecord.To<PersonRecord, PersonClass>()!;

    [Benchmark]
    public PersonClass Map_PersonRecord_To_PersonClass_Manual() => ManualMapPersonRecordToPersonClass(_personRecord);

    // 2. PersonClass.With FirstName change
    [Benchmark]
    public PersonClass With_FirstName_Library() => _personClass.With(x => x.FirstName, "Changed")!;

    [Benchmark]
    public PersonClass With_FirstName_Manual() => new PersonClass("Changed", _personClass.LastName, _personClass.Age);

    // 3. PersonRecord -> PersonClassLastNameSpelledDifferent (name map)
    [Benchmark]
    public PersonClassLastNameSpelledDifferent Map_PersonRecord_To_DifferentName_Library() =>
        _personRecord.To<PersonRecord, PersonClassLastNameSpelledDifferent>(cfg => cfg.MapName(x => x.LastName, x => x.Last_Name))!;

    [Benchmark]
    public PersonClassLastNameSpelledDifferent Map_PersonRecord_To_DifferentName_Manual() =>
        new PersonClassLastNameSpelledDifferent(_personRecord.FirstName, _personRecord.LastName, _personRecord.Age);

    // 4. MessageDto -> Message with DateTime transform
    [Benchmark]
    public Message Map_MessageDto_To_Message_Library() =>
        _messages[0].To<MessageDto, Message>(cfg => cfg.MapType<DateTime>(d => d.ToLocalTime()))!;

    [Benchmark]
    public Message Map_MessageDto_To_Message_Manual()
    {
        var dto = _messages[0];
        return new Message(dto.Msg, dto.TimeStamp.ToLocalTime(), dto.Modified.ToLocalTime());
    }

    // 5. List<MessageDto> select to Message with transform
    [Benchmark]
    public List<Message> Map_MessageDto_List_Library() => [.. _messages.Select(m => m.To<MessageDto, Message>(cfg => cfg.MapType<DateTime>(d => d.ToLocalTime()))!)];

    [Benchmark]
    public List<Message> Map_MessageDto_List_Manual()
    {
        var list = new List<Message>(_messages.Count);
        foreach (var m in _messages)
            list.Add(new Message(m.Msg, m.TimeStamp.ToLocalTime(), m.Modified.ToLocalTime()));
        return list;
    }

    // 6. Copy Master <- Slave
    [Benchmark]
    public Master Copy_Slave_To_Master_Library()
    {
        _master.Copy(_slave);
        return _master;
    }

    [Benchmark]
    public Master Copy_Slave_To_Master_Manual()
    {
        _master.Count = _slave.Count;
        _master.Item2 = _slave.Item2;
        return _master;
    }

    // 7. Copy Master <- SlaveOdd (name map + async transform removed for sync fairness)
    [Benchmark]
    public Master Copy_SlaveOdd_To_Master_Library()
    {
        _master.Copy(_slaveOdd, cfg => cfg.MapName(x => x.Counter, x => x.Count).MapName(x => x.Item_2, x => x.Item2));
        return _master;
    }

    [Benchmark]
    public Master Copy_SlaveOdd_To_Master_Manual()
    {
        _master.Count = _slaveOdd.Counter;
        _master.Item2 = _slaveOdd.Item_2;
        return _master;
    }

    private static PersonClass ManualMapPersonRecordToPersonClass(PersonRecord record) =>
        new(record.FirstName, record.LastName, record.Age);

    private static readonly object DynamicPersonRecord = new PersonRecord("John", "Doe", 23);
    private static readonly Configuration<object, PersonClass> DynamicConfig = new Configuration<object, PersonClass> { IgnoreCase = false };

    [Benchmark]
    public PersonClass Map_DynamicObject_To_PersonClass_FirstCall()
    {
        return DynamicPersonRecord.To<object, PersonClass>(_ => { })!; // uses runtime plan internally first time
    }

    [Benchmark]
    public PersonClass Map_DynamicObject_To_PersonClass_Repeated()
    {
        return DynamicPersonRecord.To<object, PersonClass>(_ => { })!; // subsequent calls hit cache
    }
}
