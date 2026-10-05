#include "document_loader.h"

static NICE_LoadedDocument g_loaded_document;

void NICE_DocumentLoaderInit(void)
{
    NICE_UnloadDocument();
}

bool NICE_LoadDocument(uint32_t page_id, const NICE_Page *page)
{
    if (page == NULL || page->blocks == NULL)
    {
        NICE_UnloadDocument();
        return false;
    }

    g_loaded_document.page_id = page_id;
    g_loaded_document.page = page;
    g_loaded_document.loaded = true;
    return true;
}

const NICE_Page *NICE_CurrentDocument(void)
{
    if (!g_loaded_document.loaded)
    {
        return NULL;
    }

    return g_loaded_document.page;
}

const NICE_LoadedDocument *NICE_CurrentLoadedDocument(void)
{
    return &g_loaded_document;
}

void NICE_UnloadDocument(void)
{
    g_loaded_document.page_id = 0;
    g_loaded_document.page = NULL;
    g_loaded_document.loaded = false;
}
