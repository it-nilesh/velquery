-- Idempotent: safe to run on every startup.
IF OBJECT_ID(N'dbo.Products') IS NULL
CREATE TABLE dbo.Products
(
    Id           int IDENTITY(1, 1) NOT NULL CONSTRAINT PK_Products PRIMARY KEY,
    Name         nvarchar(200)     NOT NULL CONSTRAINT UQ_Products_Name UNIQUE,
    Description  nvarchar(1000)    NULL,
    Price        decimal(18, 2)    NOT NULL CONSTRAINT CK_Products_Price CHECK (Price >= 0),
    Stock        int               NOT NULL CONSTRAINT CK_Products_Stock CHECK (Stock >= 0),
    CreatedAt    datetimeoffset(3) NOT NULL CONSTRAINT DF_Products_CreatedAt DEFAULT SYSDATETIMEOFFSET()
);
