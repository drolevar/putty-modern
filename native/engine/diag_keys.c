/*
 * diag_keys.c - make a throwaway SSH key for tests.
 *
 * The end-to-end tests of key authentication need a PuTTY private key (.ppk) and the matching
 * public key line for the test server's authorized keys. The Windows puttygen has no command
 * line, so the engine, which already links PuTTY's key generation and its PPK writer, does it.
 * This is a test aid, like the other pe_diag_* exports; a key generator for users is a feature
 * for later and would not look like this.
 */

#include <string.h>

#include "putty.h"
#include "ssh.h"
#include "sshkeygen.h"
#include "putty_engine.h"
#include "engine_internal.h"

PE_API int32_t pe_diag_generate_ed25519_key(const char *path_utf8, const char *passphrase_utf8,
                                            char *public_line, int32_t capacity)
{
    int32_t rc = pe_check_engine_thread();
    if (rc != PE_OK)
        return rc;
    if (!path_utf8 || !*path_utf8 || !public_line || capacity < 1)
        return PE_ERR_FAILED;

    /*
     * Key generation and the PPK writer draw on PuTTY's random number generator, which only runs
     * while somebody holds a reference (an SSH connection does). Asserts otherwise.
     */
    random_ref();

    struct eddsa_key *ek = snew(struct eddsa_key);
    if (!eddsa_generate(ek, 256)) {
        sfree(ek);
        random_unref();
        return PE_ERR_FAILED;
    }

    ssh2_userkey key;
    key.key = &ek->sshk;
    key.comment = dupstr("putty-modern TEST KEY ONLY");

    Filename *file = filename_from_str(path_utf8);
    ppk_save_parameters params = ppk_save_default_parameters;
    bool saved = ppk_save_f(file, &key, (passphrase_utf8 && *passphrase_utf8) ? passphrase_utf8 : NULL, &params);
    filename_free(file);

    /* "ssh-ed25519 AAAA...": what an authorized_keys line (and the test server's --authorized-key) takes. */
    strbuf *blob = strbuf_new();
    ssh_key_public_blob(key.key, BinarySink_UPCAST(blob));
    strbuf *line = strbuf_new();
    put_datapl(line, ptrlen_from_asciz(ssh_key_ssh_id(key.key)));
    put_byte(line, ' ');
    for (size_t i = 0; i < blob->len; i += 3) {
        char atom[4];
        size_t n = blob->len - i < 3 ? blob->len - i : 3;
        base64_encode_atom((const unsigned char *)blob->u + i, (int)n, atom);
        put_data(line, atom, 4);
    }
    put_byte(line, '\0');
    bool fits = line->len <= (size_t)capacity;
    if (fits)
        memcpy(public_line, line->s, line->len);
    strbuf_free(line);
    strbuf_free(blob);

    ssh_key_free(key.key);
    sfree(key.comment);
    random_unref();
    return (saved && fits) ? PE_OK : PE_ERR_FAILED;
}
