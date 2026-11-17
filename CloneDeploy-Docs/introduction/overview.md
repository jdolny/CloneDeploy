
# Introduction

CloneDeploy is a self-hosted platform for Windows imaging, software deployment, and endpoint management. It provides the tools to install Windows on new computers, capture and deploy operating system images, distribute software, automate maintenance, and support users, all from a central web-based interface.

Whether computers are on your local network or at remote locations, CloneDeploy allows administrators to manage devices without being physically present. Tasks can be performed on individual computers or hundreds at once, using reusable policies, schedules, and on-demand actions. From preparing new computers to maintaining existing ones, CloneDeploy helps simplify everyday IT administration.

CloneDeploy supports Windows and macOS devices, with capabilities tailored to each operating system. Built on .NET 10, the platform runs on infrastructure you control, without requiring a cloud-hosted management service.

## Who is CloneDeploy for?

CloneDeploy is designed for IT administrators and technicians who manage multiple computers and want to simplify deployment, maintenance, and support.

Common environments include:

- **Schools and universities:** Deploy and maintain classroom computers, computer labs, staff workstations, and shared devices.
- **Businesses and IT departments:** Install applications, manage configurations, automate maintenance, and support computers across multiple locations.
- **Service providers and support teams:** Manage devices, perform remote administration, and respond to user support requests from a central interface.

Whether you're managing a small collection of computers or hundreds of devices across multiple locations, CloneDeploy helps reduce repetitive work and simplify day-to-day administration.

## What can CloneDeploy do?

CloneDeploy combines operating system deployment with ongoing device management and support:

- **Windows imaging and deployment:** Capture and deploy Windows installations using WIM and FFU images. Manage image versions, deployment profiles, drivers, and Windows PE boot environments to prepare new computers, reimage existing devices, or restore standardized installations.
- **Device management:** Automatically collect hardware and software inventory, organize computers into groups, track device activity, and maintain information about managed endpoints.
- **Software deployment and automation:** Install applications, distribute files, execute scripts and commands, and automate recurring tasks using policies and schedules.
- **User self-service:** Allow users to install approved software or run authorized tasks through the Endpoint Portal without requiring direct administrator assistance.
- **Help Desk:** Let users submit support requests, communicate with technicians, and track the progress of their requests.
- **Remote administration:** Execute device actions, access remote terminal, remote viewing and control, review activity history, and manage administrative permissions.

## On-site and remote device management

CloneDeploy is designed to manage computers both on your local network and at remote locations.

Managed devices can communicate with the CloneDeploy server over an available network connection, allowing administrators to deploy software, execute policies, collect inventory, and perform maintenance regardless of where the computer is located.

This makes it possible to manage computers in classrooms, offices, remote buildings, and off-site environments through the same interface.

Optional Site Services extend capabilities such as network boot, content distribution, and device connectivity to additional locations. Imaging and other network-dependent operations require the appropriate infrastructure and connectivity.

## Windows and macOS support

CloneDeploy uses lightweight agents to communicate with managed computers and execute administrative tasks.

**Windows** provides the most comprehensive management capabilities, including software deployment, scripting, Windows Update management, printer configuration, domain joining, and operating system imaging.

**macOS** supports Apple silicon devices with inventory collection, software installation through PKG packages, scripting, file distribution, and policy execution.

Both platforms provide access to the Endpoint Portal for user self-service, notifications, and Help Desk requests.

Some features are operating system-specific. For example, Windows imaging uses Windows PE and is not available for macOS devices.

## Self-hosted by design

CloneDeploy runs on infrastructure you control, without requiring a cloud-hosted management service.

Built with .NET 10 and PostgreSQL, CloneDeploy provides a modern, cross-platform server application that can run on Windows or Linux. Your organization hosts the application server, database, and deployment storage, keeping device information, deployment files, and administrative access under your control.

The platform does not depend on a third-party cloud management service, although certain features and integrations may require internet access, such as downloading software packages, Windows updates, or connecting to Microsoft Entra.


