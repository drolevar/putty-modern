/*
 * putty_engine.h - flat C ABI of the PuTTY engine DLL.
 *
 * This is the only interface the managed side (PuTTY.Interop) sees. Keep it
 * blittable: fixed-width integers, pointers, and function pointers. No PuTTY
 * types appear here, and PuTTY headers must not be included from C++.
 */

#ifndef PUTTY_ENGINE_H
#define PUTTY_ENGINE_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(PUTTY_ENGINE_BUILD)
#  define PE_API __declspec(dllexport)
#else
#  define PE_API __declspec(dllimport)
#endif

/* Bump on any incompatible change to this header. */
#define PE_ABI_VERSION 1u

PE_API uint32_t pe_abi_version(void);

/*
 * Smoke test that exercises PuTTY's Conf store (utils/conf.c) through the
 * linked static library. Returns 1 on success, 0 on failure.
 */
PE_API int32_t pe_probe(void);

#ifdef __cplusplus
}
#endif

#endif /* PUTTY_ENGINE_H */
