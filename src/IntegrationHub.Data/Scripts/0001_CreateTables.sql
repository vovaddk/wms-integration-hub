-- Core schema. All strings are nvarchar so Dapper's default nvarchar parameters never force
-- CONVERT_IMPLICIT on an indexed column (which would turn index seeks into scans).

IF OBJECT_ID(N'dbo.products', N'U') IS NULL
CREATE TABLE dbo.products
(
    sku         nvarchar(64)  NOT NULL CONSTRAINT PK_products PRIMARY KEY,
    name        nvarchar(200) NOT NULL,
    uom         nvarchar(8)   NOT NULL,
    weight_kg   decimal(9,3)  NULL,
    barcode     nvarchar(32)  NULL,
    on_hand     int           NOT NULL CONSTRAINT DF_products_on_hand DEFAULT (0),
    created_at  datetime2(3)  NOT NULL CONSTRAINT DF_products_created_at DEFAULT (SYSUTCDATETIME()),
    updated_at  datetime2(3)  NOT NULL CONSTRAINT DF_products_updated_at DEFAULT (SYSUTCDATETIME())
);
GO

IF OBJECT_ID(N'dbo.inbox', N'U') IS NULL
CREATE TABLE dbo.inbox
(
    id               bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_inbox PRIMARY KEY,
    source_system    nvarchar(32)   NOT NULL,
    message_type     nvarchar(32)   NOT NULL,
    payload          nvarchar(max)  NOT NULL,
    correlation_id   nvarchar(64)   NOT NULL,
    status           nvarchar(16)   NOT NULL CONSTRAINT DF_inbox_status DEFAULT (N'Received')
                     CONSTRAINT CK_inbox_status CHECK (status IN (N'Received', N'Processing', N'Processed', N'Failed', N'DeadLettered')),
    attempt_count    int            NOT NULL CONSTRAINT DF_inbox_attempt_count DEFAULT (0),
    -- For Received/Failed: when the row becomes due. For Processing: when the worker's lease expires.
    next_attempt_at  datetime2(3)   NOT NULL CONSTRAINT DF_inbox_next_attempt_at DEFAULT (SYSUTCDATETIME()),
    last_error       nvarchar(2000) NULL,
    received_at      datetime2(3)   NOT NULL CONSTRAINT DF_inbox_received_at DEFAULT (SYSUTCDATETIME()),
    processed_at     datetime2(3)   NULL
);
GO

IF OBJECT_ID(N'dbo.idempotency_keys', N'U') IS NULL
CREATE TABLE dbo.idempotency_keys
(
    source_system    nvarchar(32)  NOT NULL,
    idempotency_key  nvarchar(100) NOT NULL,
    message_type     nvarchar(32)  NOT NULL,
    payload_hash     binary(32)    NOT NULL,
    inbox_id         bigint        NOT NULL CONSTRAINT FK_idempotency_keys_inbox REFERENCES dbo.inbox (id),
    created_at       datetime2(3)  NOT NULL CONSTRAINT DF_idempotency_keys_created_at DEFAULT (SYSUTCDATETIME()),
    -- The constraint, not an application pre-check, is what makes concurrent duplicates impossible.
    CONSTRAINT PK_idempotency_keys PRIMARY KEY (source_system, idempotency_key)
);
GO

IF OBJECT_ID(N'dbo.orders', N'U') IS NULL
CREATE TABLE dbo.orders
(
    id                 bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_orders PRIMARY KEY,
    source_system      nvarchar(32)      NOT NULL,
    external_order_id  nvarchar(64)      NOT NULL,
    ordered_at         datetimeoffset(3) NOT NULL,
    customer_name      nvarchar(200)     NULL,
    inbox_id           bigint            NOT NULL CONSTRAINT FK_orders_inbox REFERENCES dbo.inbox (id),
    correlation_id     nvarchar(64)      NOT NULL,
    created_at         datetime2(3)      NOT NULL CONSTRAINT DF_orders_created_at DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT UQ_orders_source_external UNIQUE (source_system, external_order_id)
);
GO

IF OBJECT_ID(N'dbo.order_lines', N'U') IS NULL
CREATE TABLE dbo.order_lines
(
    order_id  bigint       NOT NULL CONSTRAINT FK_order_lines_orders REFERENCES dbo.orders (id),
    line_no   int          NOT NULL,
    sku       nvarchar(64) NOT NULL CONSTRAINT FK_order_lines_products REFERENCES dbo.products (sku),
    quantity  int          NOT NULL CONSTRAINT CK_order_lines_quantity CHECK (quantity > 0),
    CONSTRAINT PK_order_lines PRIMARY KEY (order_id, line_no)
);
GO

IF OBJECT_ID(N'dbo.receipts', N'U') IS NULL
CREATE TABLE dbo.receipts
(
    id              bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_receipts PRIMARY KEY,
    source_system   nvarchar(32) NOT NULL,
    asn_number      nvarchar(64) NOT NULL,
    expected_on     date         NOT NULL,
    inbox_id        bigint       NOT NULL CONSTRAINT FK_receipts_inbox REFERENCES dbo.inbox (id),
    correlation_id  nvarchar(64) NOT NULL,
    received_at     datetime2(3) NOT NULL CONSTRAINT DF_receipts_received_at DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT UQ_receipts_source_asn UNIQUE (source_system, asn_number)
);
GO

IF OBJECT_ID(N'dbo.stock_movements', N'U') IS NULL
CREATE TABLE dbo.stock_movements
(
    id             bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_stock_movements PRIMARY KEY,
    sku            nvarchar(64)  NOT NULL CONSTRAINT FK_stock_movements_products REFERENCES dbo.products (sku),
    quantity       int           NOT NULL CONSTRAINT CK_stock_movements_quantity CHECK (quantity <> 0), -- + in, - out
    movement_type  nvarchar(16)  NOT NULL CONSTRAINT CK_stock_movements_type CHECK (movement_type IN (N'RECEIPT', N'ALLOCATION', N'ADJUSTMENT')),
    reference      nvarchar(100) NOT NULL,
    occurred_at    datetime2(3)  NOT NULL CONSTRAINT DF_stock_movements_occurred_at DEFAULT (SYSUTCDATETIME())
);
GO

IF OBJECT_ID(N'dbo.outbox', N'U') IS NULL
CREATE TABLE dbo.outbox
(
    id               bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_outbox PRIMARY KEY,
    event_id         uniqueidentifier NOT NULL CONSTRAINT DF_outbox_event_id DEFAULT (NEWSEQUENTIALID()),
    event_type       nvarchar(64)     NOT NULL,
    payload          nvarchar(max)    NOT NULL,
    correlation_id   nvarchar(64)     NOT NULL,
    status           nvarchar(16)     NOT NULL CONSTRAINT DF_outbox_status DEFAULT (N'Pending')
                     CONSTRAINT CK_outbox_status CHECK (status IN (N'Pending', N'Processing', N'Dispatched', N'Failed', N'DeadLettered')),
    attempt_count    int              NOT NULL CONSTRAINT DF_outbox_attempt_count DEFAULT (0),
    next_attempt_at  datetime2(3)     NOT NULL CONSTRAINT DF_outbox_next_attempt_at DEFAULT (SYSUTCDATETIME()),
    last_error       nvarchar(2000)   NULL,
    created_at       datetime2(3)     NOT NULL CONSTRAINT DF_outbox_created_at DEFAULT (SYSUTCDATETIME()),
    dispatched_at    datetime2(3)     NULL
);
GO
