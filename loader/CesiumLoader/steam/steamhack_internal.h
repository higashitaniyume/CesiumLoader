#pragma once

#include "steam_diagnostics.h"

// Private unity-build context for the Steam hook implementation.
// The implementation files are included by steamhack.cpp in dependency order.
// Hook state and POD helpers remain TU-local until each subsystem is moved behind
// an explicit interface; compiling them separately before that would duplicate
// state and break hook installation/removal ownership.