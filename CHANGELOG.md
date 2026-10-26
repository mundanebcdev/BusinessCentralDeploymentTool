# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.0] - 2026-10-26

First public release.

### Added

- `bc_list_environments` tool for listing and filtering Business Central environments.
- `bc_get_app_details` tool for reading the installed version and last update operation of one app in one environment.
- `bc_get_operation_details` tool for recovering complete Admin Center operation status, version, timestamp, schedule, and error details.
- `bc_download_symbol` tool for downloading installed app symbol packages into an allowlisted symbol cache, with at most 4 concurrent downloads to reduce endpoint rate limiting.
- `bc_deploy_pte` tool for deploying a single `.app` file or a dependency-ordered ZIP bundle to multiple environments in parallel, with every deployment schedule. Each app result includes the complete final Admin Center operation, including creation, start, and completion timestamps.
- `BC_ALLOW_PRODUCTION_DEPLOYMENT` setting, defaulting to `false`, plus a per-call `confirmProduction` argument. Both are required before any Production environment is accepted.
- JSON Lines deployment recovery journal that records accepted Admin Center operation IDs.
- Distribution as a .NET tool and MCP server package on NuGet.org.

[Unreleased]: https://github.com/mundanebcdev/BusinessCentralDeploymentTool/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/mundanebcdev/BusinessCentralDeploymentTool/releases/tag/v1.0.0
