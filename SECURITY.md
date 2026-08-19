# Security Policy

## Scope

This repository contains **sample and benchmark code only**. It is not a product, ships no service, and processes only documents you point it at on your own machine.

If you have found a vulnerability in **GroupDocs.Search itself**, please report it to GroupDocs rather than here: <https://forum.groupdocs.com/c/search> or your support contact.

For a problem in **this repository's sample code**, open a normal issue — or, if you believe disclosure would put users at risk, use GitHub's private vulnerability reporting on this repository instead of a public issue.

## Please do not include

When reporting anything here, never attach:

- A GroupDocs licence file or licence key
- Customer or otherwise confidential documents
- Absolute paths that reveal internal infrastructure

The samples deliberately read the licence from an environment variable so that it never lands in configuration, logs, or a diff. Please keep it that way in any reproduction you share.

## Supported versions

Only the latest commit on `main` is maintained. The pinned GroupDocs.Search version lives in `Directory.Packages.props`.
