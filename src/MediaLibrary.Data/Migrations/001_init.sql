-- ══════════════════════════════════════════════════════════════
--  MediaLibrary 初始 Schema
--  对应设计文档 §4.1
-- ══════════════════════════════════════════════════════════════

CREATE TABLE SchemaInfo (
    Version INTEGER NOT NULL
);
INSERT INTO SchemaInfo(Version) VALUES (1);

-- ── 素材主表 ──────────────────────────────────────────────────
CREATE TABLE Assets (
    Id            INTEGER PRIMARY KEY AUTOINCREMENT,
    Kind          TEXT    NOT NULL,            -- Video | Photo | Audio
    FileName      TEXT    NOT NULL,            -- 库内文件名：开幕式_001.mp4
    FolderKey     TEXT    NOT NULL,            -- 归一化相对目录：运动会/开幕式（根为 ''）
    LibraryPath   TEXT    NOT NULL,            -- 相对库根：运动会/开幕式/开幕式_001.mp4
    SourcePath    TEXT        NULL,            -- 原始来源绝对路径（可能已失效）
    SourceFolder  TEXT        NULL,            -- 拖入时最内层文件夹名
    SizeBytes     INTEGER NOT NULL DEFAULT 0,
    QuickHash     TEXT    NOT NULL,            -- XxHash64(size|head64K|tail64K)
    Width         INTEGER     NULL,
    Height        INTEGER     NULL,
    DurationMs    INTEGER     NULL,
    VideoCodec    TEXT        NULL,
    AudioCodec    TEXT        NULL,
    PixFmt        TEXT        NULL,
    IsHdr         INTEGER NOT NULL DEFAULT 0,
    Rotation      INTEGER NOT NULL DEFAULT 0,
    TakenAt       TEXT        NULL,            -- EXIF 拍摄时间 ISO8601
    CameraModel   TEXT        NULL,
    ThumbPath     TEXT        NULL,            -- 缩略图文件名（相对 .cache/thumbs）
    EnrichAttempts INTEGER NOT NULL DEFAULT 0, -- 元数据/缩略图补全尝试次数（≥3 则放弃）
    Status        TEXT    NOT NULL,            -- Pending|Processing|Ready|Failed|Missing
    ErrorMessage  TEXT        NULL,
    CreatedAt     TEXT    NOT NULL,
    UpdatedAt     TEXT    NOT NULL
);

CREATE UNIQUE INDEX UX_Assets_LibraryPath ON Assets(LibraryPath);
CREATE INDEX IX_Assets_FolderKey ON Assets(FolderKey);
CREATE INDEX IX_Assets_QuickHash ON Assets(QuickHash);
CREATE INDEX IX_Assets_Status    ON Assets(Status);
-- 补全队列的扫描索引
CREATE INDEX IX_Assets_Enrich    ON Assets(Status, EnrichAttempts, Id) WHERE ThumbPath IS NULL;

-- ── 命名序号计数器 ────────────────────────────────────────────
CREATE TABLE NamingCounters (
    FolderKey TEXT PRIMARY KEY,
    NextIndex INTEGER NOT NULL
);

-- ── 后台任务 ──────────────────────────────────────────────────
CREATE TABLE Jobs (
    Id           INTEGER PRIMARY KEY AUTOINCREMENT,
    AssetId      INTEGER NOT NULL REFERENCES Assets(Id) ON DELETE CASCADE,
    JobType      TEXT    NOT NULL,             -- TranscodeVideo|ConvertPhoto|CopyAudio|Thumbnail
    Status       TEXT    NOT NULL,             -- Queued|Running|Paused|Done|Failed|Canceled
    Progress     REAL    NOT NULL DEFAULT 0,
    Attempts     INTEGER NOT NULL DEFAULT 0,
    Priority     INTEGER NOT NULL DEFAULT 0,
    ErrorMessage TEXT        NULL,
    CreatedAt    TEXT    NOT NULL,
    StartedAt    TEXT        NULL,
    FinishedAt   TEXT        NULL
);
CREATE INDEX IX_Jobs_Status  ON Jobs(Status, Priority DESC, Id);
CREATE INDEX IX_Jobs_AssetId ON Jobs(AssetId);

-- ── 配置 ──────────────────────────────────────────────────────
CREATE TABLE Settings (
    Key   TEXT PRIMARY KEY,
    Value TEXT NOT NULL
);

-- ── 统计视图 ──────────────────────────────────────────────────
CREATE VIEW v_LibraryStats AS
SELECT Kind,
       COUNT(*)        AS Cnt,
       SUM(SizeBytes)  AS TotalBytes
FROM Assets
WHERE Status = 'Ready'
GROUP BY Kind;
