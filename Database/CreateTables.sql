IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014142716_InitialCreate'
)
BEGIN
    CREATE TABLE [Notifications] (
        [Id] uniqueidentifier NOT NULL,
        [CustomerId] nvarchar(100) NOT NULL,
        [Type] varchar(20) NOT NULL,
        [Recipient] nvarchar(320) NOT NULL,
        [Subject] nvarchar(200) NULL,
        [Message] nvarchar(max) NOT NULL,
        [Priority] int NOT NULL,
        [RequestedDeliveryTime] datetimeoffset NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [Fingerprint] varchar(64) NOT NULL,
        [Status] varchar(20) NOT NULL,
        [NextAttemptAt] datetimeoffset NOT NULL,
        [AttemptCount] int NOT NULL,
        [ProcessingStartedAt] datetimeoffset NULL,
        [ProcessedAt] datetimeoffset NULL,
        [FailureReason] nvarchar(1000) NULL,
        [RequestedDeliveryDate] AS CONVERT(date, [RequestedDeliveryTime]) PERSISTED,
        CONSTRAINT [PK_Notifications] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014142716_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Notifications_CustomerId] ON [Notifications] ([CustomerId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014142716_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Notifications_Fingerprint_CreatedAt] ON [Notifications] ([Fingerprint], [CreatedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014142716_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Notifications_Status_NextAttemptAt] ON [Notifications] ([Status], [NextAttemptAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014142716_InitialCreate'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261014142716_InitialCreate', N'9.0.9');
END;

COMMIT;
GO

