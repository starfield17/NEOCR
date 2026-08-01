# ADR 0004: Separately installed document engines

Status: accepted

The Apache-2.0 host defines document-source contracts but does not bundle MuPDF or another copyleft engine. Document packages are installed separately, publish their own license inventory, and communicate through the worker protocol. Users may choose an AGPL-compatible or commercially licensed package.
