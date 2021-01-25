# Migrantoid Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [2.0]
### Added
- Support for recipes (ticket #3).
### Removed
- Support for ISerializable since it is not really supported anymore by .NET Standard.
### Changed
- Surrogates are no longer used for Dictionary/HashSet serialization. This should result in a speed-up of their deserialization in certain cases (IMO the most frequently used ones) (ticket #6).

## [1.0.1] - 2021-01-02
### Fixed
- Regex serialization (ticket #2).
