#ifndef DOCUMENT_LOADER_H
#define DOCUMENT_LOADER_H

#include "nice_document.h"

#include <stdbool.h>
#include <stdint.h>

typedef struct nice_loaded_document_s
{
    uint32_t page_id;
    const NICE_Page *page;
    bool loaded;
} NICE_LoadedDocument;

void NICE_DocumentLoaderInit(void);
bool NICE_LoadDocument(uint32_t page_id, const NICE_Page *page);
const NICE_Page *NICE_CurrentDocument(void);
const NICE_LoadedDocument *NICE_CurrentLoadedDocument(void);
void NICE_UnloadDocument(void);

#endif
