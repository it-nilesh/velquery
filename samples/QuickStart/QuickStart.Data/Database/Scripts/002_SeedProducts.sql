-- Seeds sample rows only into an empty table.
IF NOT EXISTS (SELECT 1 FROM dbo.Products)
INSERT INTO dbo.Products (Name, Description, Price, Stock) VALUES
    (N'Mechanical keyboard', N'Tactile switches, 87 keys', 129.00, 25),
    (N'USB-C dock', N'Dual 4K output, 100 W pass-through', 189.50, 12),
    (N'Noise-cancelling headphones', NULL, 249.99, 7);
