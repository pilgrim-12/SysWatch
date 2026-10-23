namespace SysWatch.Agent;

public sealed class AgentOptions
{
    /// <summary>Папка для дневных SQLite-файлов.</summary>
    public string DataDir { get; set; } = @"C:\ProgramData\SysWatch\hot";

    /// <summary>Как часто опрашивать CPU/RAM/IO процессов.</summary>
    public int SampleIntervalSeconds { get; set; } = 5;

    /// <summary>Как часто сбрасывать агрегаты на диск.</summary>
    public int FlushIntervalSeconds { get; set; } = 60;

    /// <summary>PROCESS_MODE_BACKGROUND_BEGIN: низкий приоритет CPU, I/O и памяти.</summary>
    public bool BackgroundMode { get; set; } = true;

    /// <summary>EcoQoS (Windows 11): агент уходит на энергоэффективные ядра.</summary>
    public bool EcoQoS { get; set; } = true;

    /// <summary>Сохранять командные строки процессов (могут содержать секреты).</summary>
    public bool StoreCommandLines { get; set; } = true;

    /// <summary>Собирать загрузку, память и температуру видеокарты.</summary>
    public bool Gpu { get; set; } = true;

    /// <summary>Как часто опрашивать видеокарту.</summary>
    public int GpuIntervalSeconds { get; set; } = 5;

    /// <summary>Сколько дней хранить дневные файлы.</summary>
    public int RetentionDays { get; set; } = 7;
}
