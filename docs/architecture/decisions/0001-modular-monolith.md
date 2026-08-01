# ADR 0001: Modular monolith with inward dependencies

Status: accepted

NEOCR uses a .NET 10 modular monolith with Avalonia GUI and CLI composition roots. Domain/application policy stays in Contracts/Core; infrastructure, worker hosting, and platform APIs are adapters. This keeps use cases navigable for coding agents without introducing distributed-service complexity.
